using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class ChatView : UserControl
{
    private StrataChatShell? _chatShell;
    private StrataChatComposer? _welcomeComposer;
    private StrataChatComposer? _activeComposer;
    private Panel? _dropOverlay;

    private ChatViewModel? _subscribedVm;

    private readonly VoiceInputService _voiceService = new();
    private string _textBeforeVoice = "";
    private bool _voiceStarting;

    private static readonly string ClipboardImagesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumi", "clipboard-images");

    public ChatView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _chatShell = this.FindControl<StrataChatShell>("ChatShell");
        _welcomeComposer = this.FindControl<StrataChatComposer>("WelcomeComposer");
        _activeComposer = this.FindControl<StrataChatComposer>("ActiveComposer");
        _dropOverlay = this.FindControl<Panel>("DropOverlay");

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Routed events from transcript/template controls.
        AddHandler(StrataChatMessage.EditRequestedEvent, OnEditRequested);
        AddHandler(StrataChatMessage.EditConfirmedEvent, OnEditConfirmed);
        AddHandler(StrataChatMessage.RegenerateRequestedEvent, OnRegenerateRequested);
        AddHandler(StrataFileAttachment.OpenRequestedEvent, OnFileAttachmentOpenRequested);
        AddHandler(StrataFileAttachment.RemoveRequestedEvent, OnFileAttachmentRemoveRequested);

        // Routed events from composers.
        AddHandler(StrataChatComposer.SendRequestedEvent, OnComposerSendRequested);
        AddHandler(StrataChatComposer.StopRequestedEvent, OnComposerStopRequested);
        AddHandler(StrataChatComposer.AttachRequestedEvent, OnComposerAttachRequested);
        AddHandler(StrataChatComposer.AgentRemovedEvent, OnComposerAgentRemoved);
        AddHandler(StrataChatComposer.ProjectRemovedEvent, OnComposerProjectRemoved);
        AddHandler(StrataChatComposer.SkillRemovedEvent, OnComposerSkillRemoved);
        AddHandler(StrataChatComposer.McpRemovedEvent, OnComposerMcpRemoved);
        AddHandler(StrataChatComposer.VoiceRequestedEvent, OnComposerVoiceRequested);
        AddHandler(StrataChatComposer.ClipboardImagePasteRequestedEvent, OnComposerClipboardImagePasteRequested);

        WireComposerFileAutocomplete(_welcomeComposer);
        WireComposerFileAutocomplete(_activeComposer);
        WireComposerSelectionChanges(_welcomeComposer);
        WireComposerSelectionChanges(_activeComposer);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        UnsubscribeFromViewModel();

        if (DataContext is ChatViewModel vm)
        {
            _subscribedVm = vm;
            _subscribedVm.ScrollToEndRequested += OnScrollToEndRequested;
            _subscribedVm.UserMessageSent += OnUserMessageSent;
            _subscribedVm.TranscriptRebuilt += OnTranscriptRebuilt;
            _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeFromViewModel();

        if (_voiceService.IsRecording)
            _ = _voiceService.StopAsync();

        _voiceService.HypothesisGenerated -= OnVoiceHypothesis;
        _voiceService.ResultGenerated -= OnVoiceResult;
        _voiceService.Stopped -= OnVoiceStopped;
        _voiceService.Error -= OnVoiceError;
        base.OnDetachedFromVisualTree(e);
    }

    public void FocusComposer()
    {
        var composer = _subscribedVm?.IsChatVisible == true ? _activeComposer : _welcomeComposer;
        composer?.FocusInput();
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedVm is null)
            return;

        _subscribedVm.ScrollToEndRequested -= OnScrollToEndRequested;
        _subscribedVm.UserMessageSent -= OnUserMessageSent;
        _subscribedVm.TranscriptRebuilt -= OnTranscriptRebuilt;
        _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedVm = null;
    }

    private void OnScrollToEndRequested()
    {
        _chatShell?.ScrollToEnd();
    }

    private void OnUserMessageSent()
    {
        _chatShell?.ResetAutoScroll();
        _chatShell?.ScrollToEnd();
    }

    private void OnTranscriptRebuilt()
    {
        _chatShell?.ResetAutoScroll();
        Dispatcher.UIThread.Post(() =>
        {
            _chatShell?.ResetAutoScroll();
            _chatShell?.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.CurrentChat) && _subscribedVm?.CurrentChat is not null)
            _chatShell?.ResetAutoScroll();
    }

    private void WireComposerFileAutocomplete(StrataChatComposer? composer)
    {
        if (composer is null)
            return;

        composer.FileQueryChanged += OnComposerFileQueryChanged;
        composer.FileSelected += OnComposerFileSelected;
    }

    private void WireComposerSelectionChanges(StrataChatComposer? composer)
    {
        if (composer is null)
            return;

        composer.PropertyChanged += OnComposerPropertyChanged;
    }

    private void OnComposerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != StrataChatComposer.AgentNameProperty)
            return;

        if (DataContext is not ChatViewModel vm || sender is not StrataChatComposer composer)
            return;

        var isVisibleComposer = vm.IsChatVisible
            ? ReferenceEquals(composer, _activeComposer)
            : ReferenceEquals(composer, _welcomeComposer);

        if (!isVisibleComposer)
            return;

        vm.ApplyComposerAgentSelection(composer.AgentName);
    }

    private static void SyncModelFromComposer(StrataChatComposer composer, ChatViewModel vm)
    {
        var selected = composer.SelectedModel?.ToString();
        if (!string.IsNullOrEmpty(selected) && selected != vm.SelectedModel)
            vm.SelectedModel = selected;
    }

    private void OnComposerSendRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm || e.Source is not StrataChatComposer composer)
            return;

        SyncModelFromComposer(composer, vm);
        vm.SendMessageCommand.Execute(null);
    }

    private void OnComposerStopRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm)
            vm.StopGenerationCommand.Execute(null);
    }

    private async void OnComposerAttachRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm)
            await PickAndAttachFilesAsync(vm);
    }

    private void OnComposerAgentRemoved(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm)
            vm.ApplyComposerAgentSelection(null);
    }

    private void OnComposerProjectRemoved(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm)
            vm.SelectedProjectName = null;
    }

    private void OnComposerSkillRemoved(object? sender, ComposerChipRemovedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm)
            return;

        var name = e.Item is StrataComposerChip chip ? chip.Name : e.Item?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(name))
            vm.RemoveSkillByName(name);
    }

    private void OnComposerMcpRemoved(object? sender, ComposerChipRemovedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm)
            return;

        var name = e.Item is StrataComposerChip chip ? chip.Name : e.Item?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(name))
            vm.RemoveMcpByName(name);
    }

    private async void OnComposerVoiceRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm || e.Source is not StrataChatComposer composer)
            return;

        await ToggleVoiceAsync(composer, vm);
    }

    private async void OnComposerClipboardImagePasteRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm || e.Source is not StrataChatComposer composer)
            return;

        await PasteClipboardImageAsync(vm, composer);
    }

    private void OnComposerFileQueryChanged(object? sender, FileQueryChangedEventArgs e)
    {
        if (DataContext is ChatViewModel vm)
            vm.HandleFileQueryChanged(e.Query);
    }

    private void OnComposerFileSelected(object? sender, FileSelectedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm)
            return;

        vm.HandleFileSelected(e.FilePath);

        if (sender is StrataChatComposer composer)
            composer.FocusInput();
    }

    private void OnEditRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StrataChatMessage message && message.DataContext is UserMessageItem item)
            message.EditText = item.Content;
    }

    private void OnEditConfirmed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StrataChatMessage message && message.DataContext is UserMessageItem item)
            item.EditAndResend(message.EditText ?? item.Content);
    }

    private void OnRegenerateRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StrataChatMessage message && message.DataContext is UserMessageItem item)
            item.ResendFromMessage();
    }

    private void OnFileAttachmentOpenRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StrataFileAttachment { DataContext: FileAttachmentItem item })
            item.OpenCommand.Execute(null);
    }

    private void OnFileAttachmentRemoveRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StrataFileAttachment { DataContext: FileAttachmentItem item })
            item.RemoveCommand.Execute(null);
    }

    private async Task PickAndAttachFilesAsync(ChatViewModel vm)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.FilePicker_AttachFiles,
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
                vm.AddAttachment(path);
        }

        if (files.Count > 0)
        {
            if (_subscribedVm?.IsChatVisible == true)
                _activeComposer?.FocusInput();
            else
                _welcomeComposer?.FocusInput();
        }
    }

    private async Task PasteClipboardImageAsync(ChatViewModel vm, StrataChatComposer composer)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        try
        {
            var dataTransfer = await clipboard.TryGetDataAsync();
            if (dataTransfer is null)
                return;

            using var bitmap = await dataTransfer.TryGetBitmapAsync();
            if (bitmap is null)
                return;

            var filePath = SaveClipboardImage(bitmap);
            vm.AddAttachment(filePath);
            composer.FocusInput();
        }
        catch
        {
            // Ignore transient clipboard failures.
        }
    }

    private static string SaveClipboardImage(Bitmap bitmap)
    {
        Directory.CreateDirectory(ClipboardImagesDir);
        var fileName = $"clipboard-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png";
        var filePath = Path.Combine(ClipboardImagesDir, fileName);
        bitmap.Save(filePath);
        return filePath;
    }

    private async Task ToggleVoiceAsync(StrataChatComposer composer, ChatViewModel vm)
    {
        if (!_voiceService.IsAvailable || _voiceStarting)
            return;

        if (_voiceService.IsRecording)
        {
            await _voiceService.StopAsync();
            SetComposersRecording(false);
            return;
        }

        _voiceStarting = true;
        _textBeforeVoice = vm.PromptText ?? "";

        _voiceService.HypothesisGenerated += OnVoiceHypothesis;
        _voiceService.ResultGenerated += OnVoiceResult;
        _voiceService.Stopped += OnVoiceStopped;
        _voiceService.Error += OnVoiceError;

        var culture = CultureInfo.CurrentUICulture;
        var language = culture.Name.Contains('-') ? culture.Name : culture.IetfLanguageTag;
        if (string.IsNullOrEmpty(language) || !language.Contains('-'))
            language = "en-US";

        await _voiceService.StartAsync(language);

        _voiceStarting = false;
        if (_voiceService.IsRecording)
            SetComposersRecording(true);

        composer.FocusInput();
    }

    private void OnVoiceHypothesis(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribedVm is null)
                return;

            var baseText = _textBeforeVoice;
            if (!string.IsNullOrEmpty(baseText) && !baseText.EndsWith(' '))
                baseText += " ";

            _subscribedVm.PromptText = baseText + text;
        });
    }

    private void OnVoiceResult(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribedVm is null)
                return;

            var baseText = _textBeforeVoice;
            if (!string.IsNullOrEmpty(baseText) && !baseText.EndsWith(' '))
                baseText += " ";

            _textBeforeVoice = baseText + text;
            _subscribedVm.PromptText = _textBeforeVoice;
        });
    }

    private void OnVoiceError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribedVm is null)
                return;

            _subscribedVm.StatusText = message == "speech_privacy"
                ? Loc.Voice_SpeechPrivacyRequired
                : $"{Loc.Voice_Error}: {message}";
        });
    }

    private void OnVoiceStopped()
    {
        _voiceService.HypothesisGenerated -= OnVoiceHypothesis;
        _voiceService.ResultGenerated -= OnVoiceResult;
        _voiceService.Stopped -= OnVoiceStopped;
        _voiceService.Error -= OnVoiceError;

        Dispatcher.UIThread.Post(() => SetComposersRecording(false));
    }

    private void SetComposersRecording(bool recording)
    {
        if (_welcomeComposer is not null)
            _welcomeComposer.IsRecording = recording;
        if (_activeComposer is not null)
            _activeComposer.IsRecording = recording;
    }

    private static bool HasFiles(DragEventArgs e)
        => e.DataTransfer.Formats.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (HasFiles(e))
        {
            e.DragEffects = DragDropEffects.Copy;
            if (_dropOverlay is not null)
                _dropOverlay.IsVisible = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_dropOverlay is not null)
            _dropOverlay.IsVisible = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_dropOverlay is not null)
            _dropOverlay.IsVisible = false;

        if (DataContext is not ChatViewModel vm)
            return;

        foreach (var item in e.DataTransfer.Items)
        {
            if (item.TryGetRaw(DataFormat.File) is IStorageItem storageItem)
            {
                var path = storageItem.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path))
                    vm.AddAttachment(path);
            }
        }

        FocusComposer();
    }
}
