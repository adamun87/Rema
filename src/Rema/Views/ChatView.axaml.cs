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
    private Panel? _welcomePanel;
    private ChatViewModel? _wiredVm;

    public ChatView()
    {
        AvaloniaXamlLoader.Load(this);
        _chatShell = this.FindControl<StrataChatShell>("ChatShell");
        _composer = this.FindControl<StrataChatComposer>("Composer");
        _welcomePanel = this.FindControl<Panel>("WelcomePanel");
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Unsubscribe previous
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

            UpdateWelcomeVisibility(vm);
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ChatViewModel vm) return;

        if (e.PropertyName == nameof(ChatViewModel.MountedTranscriptTurns) ||
            e.PropertyName == nameof(ChatViewModel.CurrentChat))
        {
            UpdateWelcomeVisibility(vm);
        }
    }

    private void UpdateWelcomeVisibility(ChatViewModel vm)
    {
        if (_welcomePanel is not null)
            _welcomePanel.IsVisible = vm.MountedTranscriptTurns.Count == 0;
    }

    private void OnSendRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _wiredVm?.SendMessageCommand.Execute(null);
    }

    private void OnStopRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _wiredVm?.StopGenerationCommand.Execute(null);
    }

    private void OnScrollToEnd()
    {
        // Find the ScrollViewer inside StrataChatShell and scroll to end
        if (_chatShell is null) return;
        var sv = FindScrollViewer(_chatShell);
        if (sv is not null)
        {
            sv.ScrollToEnd();
        }
    }

    private void OnUserMessageSent()
    {
        OnScrollToEnd();
    }

    private void OnTranscriptRebuilt()
    {
        UpdateWelcomeVisibility(_wiredVm!);
    }

    private static ScrollViewer? FindScrollViewer(Control control)
    {
        if (control is ScrollViewer sv) return sv;
        if (control is ContentControl cc && cc.Content is Control child)
            return FindScrollViewer(child);
        if (control is Avalonia.Controls.Presenters.ContentPresenter cp && cp.Child is Control cpChild)
            return FindScrollViewer(cpChild);
        if (control is Panel panel)
        {
            foreach (var c in panel.Children)
            {
                var result = FindScrollViewer(c);
                if (result is not null) return result;
            }
        }
        // Try the visual tree
        var visualCount = Avalonia.VisualTree.VisualExtensions.GetVisualChildren(control);
        foreach (var vc in visualCount)
        {
            if (vc is ScrollViewer found) return found;
            if (vc is Control vcc)
            {
                var result = FindScrollViewer(vcc);
                if (result is not null) return result;
            }
        }
        return null;
    }
}
