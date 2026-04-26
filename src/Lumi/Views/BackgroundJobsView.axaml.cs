using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lumi.Views;

public partial class BackgroundJobsView : UserControl
{
    public BackgroundJobsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
