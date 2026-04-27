using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Rema.ViewModels;

namespace Rema.Views;

public partial class ServiceProjectsView : UserControl
{
    private ServiceProjectsViewModel? _wiredVm;

    public ServiceProjectsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_wiredVm is not null)
        {
            _wiredVm.BrowseRepoPathRequested -= OnBrowseRepoPath;
            _wiredVm.BrowseSafeFlyOutputRequested -= OnBrowseSafeFlyOutput;
        }
        if (DataContext is ServiceProjectsViewModel vm)
        {
            _wiredVm = vm;
            vm.BrowseRepoPathRequested += OnBrowseRepoPath;
            vm.BrowseSafeFlyOutputRequested += OnBrowseSafeFlyOutput;
        }
    }

    private async void OnBrowseRepoPath()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select project repository folder"
        });

        if (folders.Count > 0 && DataContext is ServiceProjectsViewModel vm)
        {
            vm.EditRepoPath = folders[0].Path.LocalPath;
        }
    }

    private async void OnBrowseSafeFlyOutput()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select SafeFly request output folder"
        });

        if (folders.Count > 0 && DataContext is ServiceProjectsViewModel vm)
            vm.SafeFlyOutputDirectory = folders[0].Path.LocalPath;
    }
}
