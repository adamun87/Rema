using System;
using Avalonia.Controls;
using Avalonia.Input;
using Rema.ViewModels;

namespace Rema.Views;

public partial class OnboardingView : UserControl
{
    private OnboardingViewModel? _wiredVm;

    public OnboardingView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_wiredVm is not null)
            _wiredVm.CopyToClipboardRequested -= OnCopyToClipboard;

        if (DataContext is OnboardingViewModel vm)
        {
            _wiredVm = vm;
            vm.CopyToClipboardRequested += OnCopyToClipboard;
        }
        else
        {
            _wiredVm = null;
        }
    }

    private async void OnCopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
        }
        catch { /* best-effort */ }
    }
}
