using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Rema.ViewModels;

namespace Rema.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SettingsViewModel vm)
            vm.ExportConfigurationRequested += OnExportConfigurationRequested;
    }

    private async void OnExportConfigurationRequested()
    {
        if (DataContext is not SettingsViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Rema configuration",
            SuggestedFileName = "rema-configuration.json",
            FileTypeChoices =
            [
                new FilePickerFileType("JSON") { Patterns = ["*.json"] }
            ]
        });

        if (file is null) return;
        await vm.ExportConfigurationAsync(file.Path.LocalPath);
    }
}
