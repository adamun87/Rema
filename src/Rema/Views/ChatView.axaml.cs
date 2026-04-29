using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Rema.ViewModels;
using StrataTheme.Controls;

namespace Rema.Views;

public partial class ChatView : UserControl
{
    private StrataChatShell? _chatShell;
    private StrataChatComposer? _composer;
    private ChatViewModel? _wiredVm;

    public ChatView()
    {
        AvaloniaXamlLoader.Load(this);
        _chatShell = this.FindControl<StrataChatShell>("ChatShell");
        _composer = this.FindControl<StrataChatComposer>("Composer");
        // Copy is handled internally by StrataChatMessage (clipboard + optional CopyCommand binding)

        // Enable drag-drop file upload
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_wiredVm is not null)
        {
            _wiredVm.ScrollToEndRequested -= OnScrollToEnd;
            _wiredVm.UserMessageSent -= OnUserMessageSent;
            _wiredVm.TranscriptRebuilt -= OnTranscriptRebuilt;
            _wiredVm.PropertyChanged -= OnVmPropertyChanged;
        }
        if (_composer is not null)
        {
            _composer.SendRequested -= OnSendRequested;
            _composer.StopRequested -= OnStopRequested;
        }

        if (DataContext is ChatViewModel vm)
        {
            _wiredVm = vm;
            vm.ScrollToEndRequested += OnScrollToEnd;
            vm.UserMessageSent += OnUserMessageSent;
            vm.TranscriptRebuilt += OnTranscriptRebuilt;
            vm.PropertyChanged += OnVmPropertyChanged;

            if (_composer is not null)
            {
                _composer.SendRequested += OnSendRequested;
                _composer.StopRequested += OnStopRequested;
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { }

    private void OnSendRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _wiredVm?.SendMessageCommand.Execute(null);

    private void OnStopRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _wiredVm?.StopGenerationCommand.Execute(null);

    private void OnScrollToEnd()
    {
        // Respects user-scrolled-away state — no-op if the user has scrolled up.
        _chatShell?.ScrollToEnd();
    }

    private void OnUserMessageSent()
    {
        // Force scroll to bottom and re-enter follow-tail mode when the user sends a message.
        _chatShell?.JumpToLatest();
    }

    private void OnTranscriptRebuilt() { }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (ctrl && e.Key == Key.F && _wiredVm is not null)
        {
            _wiredVm.ToggleSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || _wiredVm is null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .ToList();

        if (paths.Count == 0) return;

        var attachment = string.Join("\n", paths.Select(p => $"[Attached: {p}]"));
        _wiredVm.PromptText = string.IsNullOrEmpty(_wiredVm.PromptText)
            ? attachment
            : _wiredVm.PromptText + "\n" + attachment;
    }
}
