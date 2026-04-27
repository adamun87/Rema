using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
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
}
