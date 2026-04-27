using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Rema.ViewModels;

namespace Rema.Views;

public partial class ServiceProjectsView : UserControl
{
    public ServiceProjectsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ServiceProjectsViewModel vm)
            vm.BrowseRepoPathRequested += OnBrowseRepoPath;
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
}
