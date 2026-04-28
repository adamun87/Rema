using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Rema.ViewModels;

namespace Rema.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _wiredVm;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_wiredVm is not null)
        {
            _wiredVm.ExportConfigurationRequested -= OnExportConfigurationRequested;
            _wiredVm.ImportConfigurationRequested -= OnImportConfigurationRequested;
        }

        if (DataContext is SettingsViewModel vm)
        {
            _wiredVm = vm;
            vm.ExportConfigurationRequested += OnExportConfigurationRequested;
            vm.ImportConfigurationRequested += OnImportConfigurationRequested;
        }
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

    private async void OnImportConfigurationRequested()
    {
        if (DataContext is not SettingsViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Rema configuration",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON") { Patterns = ["*.json"] }
            ]
        });

        var file = files.Count == 0 ? null : files[0];
        if (file is null) return;
        await vm.ImportConfigurationAsync(file.Path.LocalPath);
    }
}
