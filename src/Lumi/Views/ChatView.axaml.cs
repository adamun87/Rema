using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class ChatView : UserControl
{
    // ── Named controls ───────────────────────────────────────────
    private StrataChatShell? _chatShell;
    private StrataChatComposer? _welcomeComposer;
    private StackPanel? _welcomeGreeting;
    private StrataChatComposer? _activeComposer;
    private StrataAttachmentList? _welcomePendingAttachmentList;
    private StrataAttachmentList? _pendingAttachmentList;
    private Panel? _welcomePanel;
    private Panel? _chatPanel;
    private Panel? _dropOverlay;
    private Panel? _loadingOverlay;

    private static readonly string ClipboardImagesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumi", "clipboard-images");

    // Track if we've already wired up event handlers
    private ChatViewModel? _subscribedVm;
    private SettingsViewModel? _settingsVm;
    private bool _suppressProjectFilterSync;

    // Voice input
    private readonly VoiceInputService _voiceService = new();
    private string _textBeforeVoice = "";
    private string _lastHypothesis = "";
    private bool _voiceStarting;

    // ── Constructor ──────────────────────────────────────────────

    public ChatView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsureSettingsSubscription();
        if (_subscribedVm is not null)
            ApplyRuntimeSettings(_subscribedVm);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _chatShell = this.FindControl<StrataChatShell>("ChatShell");
        _welcomeComposer = this.FindControl<StrataChatComposer>("WelcomeComposer");
        _welcomeGreeting = this.FindControl<StackPanel>("WelcomeGreeting");
        _activeComposer = this.FindControl<StrataChatComposer>("ActiveComposer");
        _welcomePendingAttachmentList = this.FindControl<StrataAttachmentList>("WelcomePendingAttachmentList");
        _pendingAttachmentList = this.FindControl<StrataAttachmentList>("PendingAttachmentList");
        _welcomePanel = this.FindControl<Panel>("WelcomePanel");
        _chatPanel = this.FindControl<Panel>("ChatPanel");
        _dropOverlay = this.FindControl<Panel>("DropOverlay");
        _loadingOverlay = this.FindControl<Panel>("LoadingOverlay");

        // Drag & drop
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Bubbled RoutedEvents from DataTemplate-created controls
        AddHandler(StrataChatMessage.EditRequestedEvent, OnEditRequested);
        AddHandler(StrataChatMessage.EditConfirmedEvent, OnEditConfirmed);
        AddHandler(StrataChatMessage.RegenerateRequestedEvent, OnRegenerateRequested);
        AddHandler(StrataFileAttachment.OpenRequestedEvent, OnFileChipOpenRequested);
    }

    // ── DataContext wiring ───────────────────────────────────────

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not ChatViewModel vm) return;

        if (vm == _subscribedVm)
        {
            EnsureSettingsSubscription();
            return;
        }
        _subscribedVm = vm;
        EnsureSettingsSubscription();
        ApplyRuntimeSettings(vm);

        // Scroll & reset
        vm.ScrollToEndRequested += () => _chatShell?.ScrollToEnd();
        vm.UserMessageSent += () =>
        {
            _chatShell?.ResetAutoScroll();
            _chatShell?.ScrollToEnd();
        };

        // Transcript rebuild event from ViewModel — scroll to bottom once after layout.
        // StrataChatShell.ScrollToEnd already debounces and posts a follow-up
        // scroll at Loaded priority to adjust for extent changes from realization.
        // Do NOT chain additional dispatched ScrollToEnd calls — that creates a
        // cascade where each scroll triggers realization, changing the extent,
        // triggering another scroll, etc.
        vm.TranscriptRebuilt += () =>
        {
            _chatShell?.ResetAutoScroll();
            Dispatcher.UIThread.Post(() =>
            {
                _chatShell?.ResetAutoScroll();
                _chatShell?.ScrollToEnd();
            }, DispatcherPriority.Loaded);
        };

        // Composer catalogs
        PopulateComposerCatalogs(vm);
        UpdateComposerAgent(vm.ActiveAgent);
        vm.AgentChanged += () => UpdateComposerAgent(vm.ActiveAgent);

        // Browser toggle
        var browserToggle = this.FindControl<Button>("BrowserToggleButton");
        if (browserToggle is not null)
            browserToggle.Click += (_, _) => vm.ToggleBrowser();

        // Wire composers
        WireComposer(_welcomeComposer, vm);
        WireComposer(_activeComposer, vm);

        // Pending attachments
        vm.PendingAttachments.CollectionChanged += (_, _) => RebuildPendingAttachmentChips(vm);

        // Skill chips
        vm.ActiveSkillChips.CollectionChanged += (_, args) =>
        {
            if (vm.IsLoadingChat) return;
            if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
            {
                foreach (var item in args.NewItems)
                {
                    if (item is StrataComposerChip chip)
                        vm.RegisterSkillIdByName(chip.Name);
                }
            }
        };

        // MCP chips
        vm.ActiveMcpChips.CollectionChanged += (_, args) =>
        {
            if (vm.IsLoadingChat) return;
            if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
            {
                foreach (var item in args.NewItems)
                {
                    if (item is StrataComposerChip chip)
                        vm.RegisterMcpByName(chip.Name);
                }
            }
            else if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems is not null)
            {
                foreach (var item in args.OldItems)
                {
                    if (item is StrataComposerChip chip)
                    {
                        vm.ActiveMcpServerNames.Remove(chip.Name);
                        vm.SyncActiveMcpsToChat();
                    }
                }
            }
        };

        // IsBusy → interaction surface opacity
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.IsBusy))
                ApplyInteractionSurfaceBusyState(vm.IsBusy);
        };

        // CurrentChat → panel switching
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.CurrentChat))
                OnCurrentChatChanged(vm);
            else if (args.PropertyName is nameof(ChatViewModel.HasUsedBrowser)
                     or nameof(ChatViewModel.IsBrowserOpen))
                UpdateBrowserToggle(vm);
            else if (args.PropertyName == nameof(ChatViewModel.SelectedModel))
                UpdateQualityLevels(vm.SelectedModel);
        };

        ApplyInteractionSurfaceBusyState(vm.IsBusy);
    }

    // ── Composer wiring ──────────────────────────────────────────

    private void WireComposer(StrataChatComposer? composer, ChatViewModel vm)
    {
        if (composer is null) return;

        composer.SendRequested += (_, _) =>
        {
            SyncModelFromComposer(composer, vm);
            vm.SendMessageCommand.Execute(null);
        };
        composer.StopRequested += (_, _) => vm.StopGenerationCommand.Execute(null);
        composer.AttachRequested += (_, _) => _ = PickAndAttachFilesAsync(vm);
        composer.AgentRemoved += (_, _) => vm.SetActiveAgent(null);
        composer.ProjectRemoved += (_, _) =>
        {
            vm.ClearProjectId();
            UpdateComposerProject(vm);
            if (FindMainViewModel() is { } mainVm)
                mainVm.ClearProjectFilterCommand.Execute(null);
        };
        composer.SkillRemoved += (_, args) =>
        {
            if (args is ComposerChipRemovedEventArgs chipArgs)
            {
                var name = chipArgs.Item is StrataComposerChip sc ? sc.Name : chipArgs.Item?.ToString() ?? "";
                vm.RemoveSkillByName(name);
            }
        };
        composer.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name == "AgentName")
                OnComposerAgentChanged(composer, vm);
            else if (args.Property.Name == "ProjectName")
                OnComposerProjectChanged(composer, vm);
        };
        composer.VoiceRequested += (_, _) => _ = ToggleVoiceAsync(composer, vm);
        WireClipboardImagePaste(composer, vm);
        WireFileAutoComplete(composer, vm);
    }

    // ── RoutedEvent handlers for transcript controls ─────────────

    private void OnEditRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StrataChatMessage msg && msg.DataContext is UserMessageItem item)
            msg.EditText = item.Content;
    }

    private void OnEditConfirmed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StrataChatMessage msg && msg.DataContext is UserMessageItem item)
            item.EditAndResend(msg.EditText ?? item.Content);
    }

    private void OnRegenerateRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StrataChatMessage msg && msg.DataContext is UserMessageItem item)
            item.ResendFromMessage();
    }

    private void OnFileChipOpenRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StrataFileAttachment chip && chip.DataContext is FileAttachmentItem fileItem)
            fileItem.OpenCommand.Execute(null);
    }

    // ── Panel switching ──────────────────────────────────────────

    private void OnCurrentChatChanged(ChatViewModel vm)
    {
        var hasChat = vm.CurrentChat is not null;
        var fromWelcome = _welcomePanel?.IsVisible == true && hasChat;
        var toWelcome = _chatPanel?.IsVisible == true && !hasChat;

        if (_welcomePanel is not null) _welcomePanel.IsVisible = !hasChat;
        if (_chatPanel is not null) _chatPanel.IsVisible = hasChat;

        // Animate composer expand when transitioning from welcome
        if (fromWelcome && _activeComposer is not null)
        {
            var v = ElementComposition.GetElementVisual(_activeComposer);
            if (v is not null)
                v.Scale = new System.Numerics.Vector3(0.92f, 1f, 1f);
            Dispatcher.UIThread.Post(AnimateComposerExpand, DispatcherPriority.Loaded);
        }

        // Animate composer contract when returning to welcome
        if (toWelcome && _welcomeComposer is not null && _activeComposer is not null)
        {
            var chatWidth = _activeComposer.Bounds.Width;
            var welcomeMax = 680.0;
            var startScaleX = chatWidth > 0 ? (float)Math.Min(chatWidth / welcomeMax, 1.5) : 1.1f;
            var v = ElementComposition.GetElementVisual(_welcomeComposer);
            if (v is not null)
                v.Scale = new System.Numerics.Vector3(startScaleX, 1f, 1f);
            var capturedScale = startScaleX;
            Dispatcher.UIThread.Post(() => AnimateComposerContract(capturedScale), DispatcherPriority.Loaded);
        }

        if (toWelcome && _welcomeGreeting is not null)
            AnimateControlEntrance(_welcomeGreeting);

        // The ViewModel rebuilds transcript via RebuildTranscript()

        UpdateProjectBadge(vm);
        UpdateComposerProject(vm);
        UpdateBrowserToggle(vm);

        if (hasChat)
            _chatShell?.ResetAutoScroll();
    }

    // ── Settings ─────────────────────────────────────────────────

    private void EnsureSettingsSubscription()
    {
        var mainVm = FindMainViewModel();
        var nextSettingsVm = mainVm?.SettingsVM;
        if (ReferenceEquals(_settingsVm, nextSettingsVm)) return;

        if (_settingsVm is not null)
            _settingsVm.PropertyChanged -= OnSettingsViewModelPropertyChanged;
        _settingsVm = nextSettingsVm;
        if (_settingsVm is not null)
            _settingsVm.PropertyChanged += OnSettingsViewModelPropertyChanged;
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_subscribedVm is null) return;

        if (e.PropertyName is nameof(SettingsViewModel.SendWithEnter)
            or nameof(SettingsViewModel.PreferredModel))
            ApplyRuntimeSettings(_subscribedVm);

        if (e.PropertyName is nameof(SettingsViewModel.ShowTimestamps)
            or nameof(SettingsViewModel.ShowToolCalls)
            or nameof(SettingsViewModel.ShowReasoning))
            _subscribedVm.RebuildTranscript();
    }

    private void ApplyRuntimeSettings(ChatViewModel vm)
    {
        var settings = _settingsVm;
        if (settings is null) return;

        if (_welcomeComposer is not null)
            _welcomeComposer.SendWithEnter = settings.SendWithEnter;
        if (_activeComposer is not null)
            _activeComposer.SendWithEnter = settings.SendWithEnter;

        if (!string.IsNullOrWhiteSpace(settings.PreferredModel)
            && vm.SelectedModel != settings.PreferredModel)
            vm.SelectedModel = settings.PreferredModel;
    }

    // ── Badge & composer state ───────────────────────────────────

    private MainViewModel? FindMainViewModel()
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        return window?.DataContext as MainViewModel;
    }

    private void UpdateProjectBadge(ChatViewModel vm)
    {
        var badge = this.FindControl<Avalonia.Controls.Border>("ProjectBadge");
        var badgeText = this.FindControl<Avalonia.Controls.TextBlock>("ProjectBadgeText");
        if (badge is null) return;

        var projectId = vm.CurrentChat?.ProjectId;
        if (projectId.HasValue)
        {
            var mainVm = FindMainViewModel();
            var projectName = mainVm?.GetProjectName(projectId);
            badge.IsVisible = projectName is not null;
            if (badgeText is not null)
                badgeText.Text = $"📁 {projectName}";
        }
        else
        {
            badge.IsVisible = false;
        }
    }

    private void UpdateBrowserToggle(ChatViewModel vm)
    {
        var btn = this.FindControl<Button>("BrowserToggleButton");
        if (btn is null) return;

        btn.IsVisible = vm.HasUsedBrowser;
        var text = this.FindControl<TextBlock>("BrowserToggleText");
        var isOpen = vm.IsBrowserOpen;

        if (isOpen)
        {
            if (this.TryFindResource("Brush.AccentSubtle", out var bg) && bg is IBrush bgBrush)
                btn.Background = bgBrush;
            if (text is not null && this.TryFindResource("Brush.AccentDefault", out var fg) && fg is IBrush fgBrush)
                text.Foreground = fgBrush;
        }
        else
        {
            btn.ClearValue(Button.BackgroundProperty);
            if (text is not null)
                text.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private void UpdateComposerAgent(LumiAgent? agent)
    {
        var name = agent?.Name;
        var glyph = agent?.IconGlyph ?? "◉";

        if (_welcomeComposer is not null)
        {
            _welcomeComposer.AgentName = name;
            _welcomeComposer.AgentGlyph = glyph;
        }
        if (_activeComposer is not null)
        {
            _activeComposer.AgentName = name;
            _activeComposer.AgentGlyph = glyph;
        }

        var badge = this.FindControl<Avalonia.Controls.Border>("AgentBadge");
        var badgeText = this.FindControl<Avalonia.Controls.TextBlock>("AgentBadgeText");
        if (badge is not null)
        {
            badge.IsVisible = agent is not null;
            if (badgeText is not null && agent is not null)
                badgeText.Text = $"{agent.IconGlyph} {agent.Name}";
        }
    }

    private void OnComposerAgentChanged(StrataChatComposer composer, ChatViewModel vm)
    {
        var agentName = composer.AgentName;
        if (vm.ActiveAgent?.Name == agentName) return;

        if (!vm.CanChangeAgent)
        {
            composer.AgentName = vm.ActiveAgent?.Name;
            composer.AgentGlyph = vm.ActiveAgent?.IconGlyph ?? "◉";
            return;
        }

        if (string.IsNullOrEmpty(agentName))
        {
            vm.SetActiveAgent(null);
        }
        else
        {
            vm.SelectAgentByName(agentName);
            var other = composer == _welcomeComposer ? _activeComposer : _welcomeComposer;
            if (other is not null)
            {
                other.AgentName = composer.AgentName;
                other.AgentGlyph = composer.AgentGlyph;
            }
        }
    }

    private void OnComposerProjectChanged(StrataChatComposer composer, ChatViewModel vm)
    {
        if (_suppressProjectFilterSync) return;

        var projectName = composer.ProjectName;
        if (string.IsNullOrEmpty(projectName))
        {
            vm.ClearProjectId();
            if (FindMainViewModel() is { } mainVm)
                mainVm.ClearProjectFilterCommand.Execute(null);
        }
        else
        {
            var isExistingChat = vm.CurrentChat is not null && vm.CurrentChat.Messages.Count > 0;
            if (!isExistingChat)
                vm.SelectProjectByName(projectName);

            var other = composer == _welcomeComposer ? _activeComposer : _welcomeComposer;
            if (other is not null)
            {
                _suppressProjectFilterSync = true;
                other.ProjectName = projectName;
                _suppressProjectFilterSync = false;
            }

            if (FindMainViewModel() is { } mainVm)
            {
                var project = mainVm.DataStore.Data.Projects.FirstOrDefault(p => p.Name == projectName);
                if (project is not null)
                    mainVm.SelectProjectFilterCommand.Execute(project);
            }

            Dispatcher.UIThread.Post(() => composer.FocusInput(), DispatcherPriority.Input);
        }
        UpdateProjectBadge(vm);
    }

    private void UpdateComposerProject(ChatViewModel vm)
    {
        _suppressProjectFilterSync = true;
        try
        {
            var name = vm.GetCurrentProjectName();
            if (_welcomeComposer is not null) _welcomeComposer.ProjectName = name;
            if (_activeComposer is not null) _activeComposer.ProjectName = name;
        }
        finally { _suppressProjectFilterSync = false; }
    }

    public void FocusComposer()
    {
        var composer = _chatPanel?.IsVisible == true ? _activeComposer : _welcomeComposer;
        composer?.FocusInput();
    }

    public void PopulateComposerCatalogs(ChatViewModel vm)
    {
        var agentChips = vm.GetAgentChips();
        var skillChips = vm.GetSkillChips();
        var mcpChips = vm.GetMcpChips();
        var projectChips = vm.GetProjectChips();

        if (_welcomeComposer is not null)
        {
            _welcomeComposer.AvailableAgents = agentChips;
            _welcomeComposer.AvailableSkills = skillChips;
            _welcomeComposer.AvailableMcps = mcpChips;
            _welcomeComposer.AvailableProjects = projectChips;
        }
        if (_activeComposer is not null)
        {
            _activeComposer.AvailableAgents = agentChips;
            _activeComposer.AvailableSkills = skillChips;
            _activeComposer.AvailableMcps = mcpChips;
            _activeComposer.AvailableProjects = projectChips;
        }
    }

    private static void SyncModelFromComposer(StrataChatComposer composer, ChatViewModel vm)
    {
        var selected = composer.SelectedModel?.ToString();
        if (!string.IsNullOrEmpty(selected) && selected != vm.SelectedModel)
            vm.SelectedModel = selected;
    }

    // ── Quality levels ───────────────────────────────────────────

    private static string[] ReasoningLevels => [Loc.Quality_Low, Loc.Quality_Medium, Loc.Quality_High];

    private static bool IsReasoningModel(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return false;
        return modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("think", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateQualityLevels(string? modelId)
    {
        var levels = IsReasoningModel(modelId) ? ReasoningLevels : null;
        if (_welcomeComposer is not null) _welcomeComposer.QualityLevels = levels;
        if (_activeComposer is not null) _activeComposer.QualityLevels = levels;
    }

    // ── Interaction surface ──────────────────────────────────────

    private void ApplyInteractionSurfaceBusyState(bool isBusy)
    {
        var targetOpacity = isBusy ? 0.965f : 1f;
        var durationMs = isBusy ? 140 : 200;
        AnimateSurfaceOpacity(_welcomeComposer, targetOpacity, durationMs);
        AnimateSurfaceOpacity(_activeComposer, targetOpacity, durationMs);
        AnimateSurfaceOpacity(_welcomePendingAttachmentList, targetOpacity, durationMs);
        AnimateSurfaceOpacity(_pendingAttachmentList, targetOpacity, durationMs);
    }

    private static void AnimateSurfaceOpacity(Control? control, float targetOpacity, int durationMs)
    {
        if (control is null) return;
        var visual = ElementComposition.GetElementVisual(control);
        var compositor = visual?.Compositor;
        if (visual is null || compositor is null) { control.Opacity = targetOpacity; return; }

        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.Target = "Opacity";
        anim.InsertKeyFrame(1f, targetOpacity);
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Opacity", anim);
    }

    // ── Pending attachments ──────────────────────────────────────

    private void RebuildPendingAttachmentChips(ChatViewModel vm)
    {
        UpdateAttachmentList(_pendingAttachmentList, vm);
        UpdateAttachmentList(_welcomePendingAttachmentList, vm);
    }

    private static void UpdateAttachmentList(StrataAttachmentList? list, ChatViewModel vm)
    {
        if (list is null) return;
        list.Items.Clear();

        foreach (var filePath in vm.PendingAttachments)
        {
            var chip = CreatePendingFileChip(filePath);
            chip.RemoveRequested += (_, _) => vm.RemoveAttachment(filePath);
            list.Items.Add(chip);
        }
        list.IsVisible = vm.PendingAttachments.Count > 0;
    }

    private static StrataFileAttachment CreatePendingFileChip(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        string? fileSize = null;
        try
        {
            var info = new FileInfo(filePath);
            if (info.Exists)
                fileSize = FormatFileSize(info.Length);
        }
        catch { }

        var chip = new StrataFileAttachment
        {
            FileName = fileName,
            FileSize = fileSize,
            Status = StrataAttachmentStatus.Completed,
            IsRemovable = true
        };
        chip.OpenRequested += (_, _) => OpenFileInSystem(filePath);
        return chip;
    }

    private static void OpenFileInSystem(string filePath)
    {
        try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
        catch { }
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => string.Format(Loc.FileSize_B, bytes),
        < 1024 * 1024 => string.Format(Loc.FileSize_KB, $"{bytes / 1024.0:F1}"),
        < 1024 * 1024 * 1024 => string.Format(Loc.FileSize_MB, $"{bytes / (1024.0 * 1024):F1}"),
        _ => string.Format(Loc.FileSize_GB, $"{bytes / (1024.0 * 1024 * 1024):F2}")
    };

    // ── File picker ──────────────────────────────────────────────

    private async Task PickAndAttachFilesAsync(ChatViewModel vm)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.FilePicker_AttachFiles,
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null)
                vm.AddAttachment(path);
        }

        if (files.Count > 0)
        {
            var composer = _activeComposer ?? _welcomeComposer;
            composer?.FocusInput();
        }
    }

    // ── Clipboard paste ──────────────────────────────────────────

    private void WireClipboardImagePaste(StrataChatComposer composer, ChatViewModel vm)
    {
        var eventInfo = typeof(StrataChatComposer).GetEvent("ClipboardImagePasteRequested");
        if (eventInfo is null) return;
        EventHandler<RoutedEventArgs> handler = (_, _) => _ = PasteClipboardImageAsync(vm, composer);
        eventInfo.AddEventHandler(composer, handler);
    }

    private async Task PasteClipboardImageAsync(ChatViewModel vm, StrataChatComposer? composer)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            var dataTransfer = await clipboard.TryGetDataAsync();
            if (dataTransfer is null) return;
            using var bitmap = await dataTransfer.TryGetBitmapAsync();
            if (bitmap is null) return;
            var filePath = SaveClipboardImage(bitmap);
            vm.AddAttachment(filePath);
            composer?.FocusInput();
        }
        catch { }
    }

    private static string SaveClipboardImage(Bitmap bitmap)
    {
        Directory.CreateDirectory(ClipboardImagesDir);
        var fileName = $"clipboard-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png";
        var filePath = Path.Combine(ClipboardImagesDir, fileName);
        bitmap.Save(filePath);
        return filePath;
    }

    // ── File auto-complete ───────────────────────────────────────

    private CancellationTokenSource? _fileSearchCts;

    private void WireFileAutoComplete(StrataChatComposer composer, ChatViewModel vm)
    {
        composer.FileQueryChanged += (_, args) =>
        {
            _fileSearchCts?.Cancel();
            var cts = new CancellationTokenSource();
            _fileSearchCts = cts;
            _ = Task.Run(() =>
            {
                if (cts.Token.IsCancellationRequested) return;
                var results = vm.SearchFiles(args.Query);
                if (cts.Token.IsCancellationRequested) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.Token.IsCancellationRequested) return;
                    composer.AvailableFiles = results;
                });
            }, cts.Token);
        };

        composer.FileSelected += (_, args) =>
        {
            vm.AddAttachment(args.FilePath);
            composer.FocusInput();
        };
    }

    // ── Voice input ──────────────────────────────────────────────

    private async Task ToggleVoiceAsync(StrataChatComposer composer, ChatViewModel vm)
    {
        if (!_voiceService.IsAvailable || _voiceStarting) return;

        if (_voiceService.IsRecording)
        {
            await _voiceService.StopAsync();
            SetComposersRecording(false);
            return;
        }

        _voiceStarting = true;
        _textBeforeVoice = vm.PromptText ?? "";
        _lastHypothesis = "";

        _voiceService.HypothesisGenerated += OnVoiceHypothesis;
        _voiceService.ResultGenerated += OnVoiceResult;
        _voiceService.Stopped += OnVoiceStopped;
        _voiceService.Error += OnVoiceError;

        var culture = CultureInfo.CurrentUICulture;
        var lang = culture.Name.Contains('-') ? culture.Name : culture.IetfLanguageTag;
        if (string.IsNullOrEmpty(lang) || !lang.Contains('-')) lang = "en-US";
        await _voiceService.StartAsync(lang);

        _voiceStarting = false;
        if (_voiceService.IsRecording)
            SetComposersRecording(true);
    }

    private void OnVoiceHypothesis(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribedVm is null) return;
            var baseText = _textBeforeVoice;
            if (!string.IsNullOrEmpty(baseText) && !baseText.EndsWith(' ')) baseText += " ";
            _lastHypothesis = text;
            _subscribedVm.PromptText = baseText + text;
        });
    }

    private void OnVoiceResult(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribedVm is null) return;
            var baseText = _textBeforeVoice;
            if (!string.IsNullOrEmpty(baseText) && !baseText.EndsWith(' ')) baseText += " ";
            _textBeforeVoice = baseText + text;
            _lastHypothesis = "";
            _subscribedVm.PromptText = _textBeforeVoice;
        });
    }

    private void OnVoiceError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribedVm is null) return;
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
        if (_welcomeComposer is not null) _welcomeComposer.IsRecording = recording;
        if (_activeComposer is not null) _activeComposer.IsRecording = recording;
    }

    // ── Drag & drop ──────────────────────────────────────────────

    private static bool HasFiles(DragEventArgs e) =>
        e.DataTransfer.Formats.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (HasFiles(e))
        {
            e.DragEffects = DragDropEffects.Copy;
            if (_dropOverlay is not null) _dropOverlay.IsVisible = true;
        }
        else
            e.DragEffects = DragDropEffects.None;
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_dropOverlay is not null) _dropOverlay.IsVisible = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_dropOverlay is not null) _dropOverlay.IsVisible = false;
        if (DataContext is not ChatViewModel vm) return;

        foreach (var item in e.DataTransfer.Items)
        {
            if (item.TryGetRaw(DataFormat.File) is IStorageItem storageItem)
            {
                var path = storageItem.TryGetLocalPath();
                if (path is not null)
                    vm.AddAttachment(path);
            }
        }

        var composer = _chatPanel?.IsVisible == true ? _activeComposer : _welcomeComposer;
        composer?.FocusInput();
    }

    // ── Animations ───────────────────────────────────────────────

    private async void AnimateControlEntrance(Control control, double startOffsetY = 16, int durationMs = 300)
    {
        control.Opacity = 0;
        control.RenderTransform = new TranslateTransform(0, startOffsetY);

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            FillMode = Avalonia.Animation.FillMode.Forward,
            Children =
            {
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(TranslateTransform.YProperty, startOffsetY),
                    }
                },
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(TranslateTransform.YProperty, 0.0),
                    }
                },
            }
        };

        try { await anim.RunAsync(control); }
        catch { }

        control.Opacity = 1;
        control.RenderTransform = null;
    }

    private void AnimateComposerExpand()
    {
        if (_activeComposer is null) return;
        var visual = ElementComposition.GetElementVisual(_activeComposer);
        if (visual is null) return;
        var comp = visual.Compositor;
        var chatWidth = _activeComposer.Bounds.Width;
        var scaleX = chatWidth > 0 ? (float)Math.Min(680.0 / chatWidth, 1.0) : 0.92f;

        visual.CenterPoint = new Avalonia.Vector3D(
            chatWidth / 2, _activeComposer.Bounds.Height / 2, 0);

        var scaleAnim = comp.CreateVector3KeyFrameAnimation();
        scaleAnim.Target = "Scale";
        scaleAnim.InsertKeyFrame(0f, new System.Numerics.Vector3(scaleX, 1f, 1f));
        scaleAnim.InsertKeyFrame(0.7f, new System.Numerics.Vector3(1.006f, 1f, 1f));
        scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1f, 1f, 1f));
        scaleAnim.Duration = TimeSpan.FromMilliseconds(380);
        visual.StartAnimation("Scale", scaleAnim);
    }

    private void AnimateComposerContract(float startScaleX)
    {
        if (_welcomeComposer is null) return;
        var visual = ElementComposition.GetElementVisual(_welcomeComposer);
        if (visual is null) return;
        var comp = visual.Compositor;
        var w = _welcomeComposer.Bounds.Width;

        visual.CenterPoint = new Avalonia.Vector3D(w / 2, _welcomeComposer.Bounds.Height / 2, 0);

        var scaleAnim = comp.CreateVector3KeyFrameAnimation();
        scaleAnim.Target = "Scale";
        scaleAnim.InsertKeyFrame(0f, new System.Numerics.Vector3(startScaleX, 1f, 1f));
        scaleAnim.InsertKeyFrame(0.7f, new System.Numerics.Vector3(0.996f, 1f, 1f));
        scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1f, 1f, 1f));
        scaleAnim.Duration = TimeSpan.FromMilliseconds(380);
        visual.StartAnimation("Scale", scaleAnim);
    }
}
