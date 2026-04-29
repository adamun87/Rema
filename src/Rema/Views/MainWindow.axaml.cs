using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Rema.Services;
using Rema.ViewModels;

namespace Rema.Views;

public partial class MainWindow : Window
{
    private Control?[] _pages = [];
    private Control?[] _sidebarPanels = [];
    private Button?[] _navButtons = [];
    private Control? _onboardingPanel;
    private MainViewModel? _wiredVm;
    private GlobalHotkeyService? _hotkeyService;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Cache named controls
        _onboardingPanel = this.FindControl<Control>("OnboardingPanel");

        _pages =
        [
            this.FindControl<Control>("Page0"),
            this.FindControl<Control>("Page1"),
            this.FindControl<Control>("Page2"),
            this.FindControl<Control>("Page3"),
            this.FindControl<Control>("Page4"),
            this.FindControl<Control>("Page5"),
            this.FindControl<Control>("Page6"),
            this.FindControl<Control>("Page7"),
            this.FindControl<Control>("Page8"),
            this.FindControl<Control>("Page9"),
        ];

        _sidebarPanels =
        [
            this.FindControl<Control>("Sidebar0"),
            this.FindControl<Control>("Sidebar1"),
            this.FindControl<Control>("Sidebar2"),
            this.FindControl<Control>("Sidebar3"),
            this.FindControl<Control>("Sidebar4"),
            this.FindControl<Control>("Sidebar5"),
            this.FindControl<Control>("Sidebar6"),
            this.FindControl<Control>("Sidebar7"),
            this.FindControl<Control>("Sidebar8"),
            this.FindControl<Control>("Sidebar9"),
        ];

        _navButtons =
        [
            this.FindControl<Button>("NavDashboard"),
            this.FindControl<Button>("NavShift"),
            this.FindControl<Button>("NavChat"),
            this.FindControl<Button>("NavProjects"),
            this.FindControl<Button>("NavMemories"),
            this.FindControl<Button>("NavSkills"),
            this.FindControl<Button>("NavMcpServers"),
            this.FindControl<Button>("NavTools"),
            this.FindControl<Button>("NavAgents"),
            this.FindControl<Button>("NavSettings"),
        ];

        Opened += (_, _) =>
        {
            RestoreWindowBounds();
            _hotkeyService = new GlobalHotkeyService();
            _hotkeyService.Start(this);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_wiredVm is not null)
            _wiredVm.PropertyChanged -= OnVmPropertyChanged;

        if (DataContext is MainViewModel vm)
        {
            _wiredVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            UpdateOnboardingVisibility(vm);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;

        if (e.PropertyName == nameof(MainViewModel.IsOnboarded))
            UpdateOnboardingVisibility(vm);
        else if (e.PropertyName == nameof(MainViewModel.SelectedNavIndex))
            ShowPage(vm.SelectedNavIndex);
    }

    private void UpdateOnboardingVisibility(MainViewModel vm)
    {
        if (_onboardingPanel is not null)
            _onboardingPanel.IsVisible = !vm.IsOnboarded;
    }

    // ── Navigation ──

    private void NavButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        int index;
        if (btn.Tag is int intTag)
            index = intTag;
        else if (btn.Tag is string tagStr && int.TryParse(tagStr, out var parsed))
            index = parsed;
        else
            return;

        ShowPage(index);
    }

    private void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Length)
            return;

        // Update page visibility
        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] is not null)
                _pages[i]!.IsVisible = i == index;
        }

        // Update sidebar panel visibility
        for (int i = 0; i < _sidebarPanels.Length; i++)
        {
            if (_sidebarPanels[i] is not null)
                _sidebarPanels[i]!.IsVisible = i == index;
        }

        // Update nav button active class
        for (int i = 0; i < _navButtons.Length; i++)
        {
            if (_navButtons[i] is not null)
            {
                if (i == index)
                    _navButtons[i]!.Classes.Add("active");
                else
                    _navButtons[i]!.Classes.Remove("active");
            }
        }

        // Sync to ViewModel
        if (DataContext is MainViewModel vm)
            vm.SelectedNavIndex = index;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        CaptureBoundsToSettings();
        _hotkeyService?.Dispose();
        base.OnClosing(e);
    }

    private void RestoreWindowBounds()
    {
        if (DataContext is not MainViewModel vm) return;
        var settings = vm.DataStore.Data.Settings;

        const double defaultWidth = 1200;
        const double defaultHeight = 800;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            Width = defaultWidth;
            Height = defaultHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var scaling = screen.Scaling;
        if (scaling <= 0) scaling = 1.0;

        var workArea = screen.WorkingArea;
        var maxW = Math.Max(1.0, workArea.Width / scaling);
        var maxH = Math.Max(1.0, workArea.Height / scaling);

        var w = settings.WindowWidth ?? defaultWidth;
        var h = settings.WindowHeight ?? defaultHeight;

        var minW = Math.Min(MinWidth, maxW);
        var minH = Math.Min(MinHeight, maxH);
        w = Math.Clamp(w, minW, maxW);
        h = Math.Clamp(h, minH, maxH);

        Width = w;
        Height = h;

        if (settings.WindowLeft.HasValue && settings.WindowTop.HasValue)
        {
            var left = settings.WindowLeft.Value;
            var top = settings.WindowTop.Value;

            // Ensure at least 100px of the window is visible on any screen
            bool isVisible = false;
            foreach (var s in Screens.All)
            {
                var wa = s.WorkingArea;
                var waLeft = wa.X / s.Scaling;
                var waTop = wa.Y / s.Scaling;
                var waRight = waLeft + wa.Width / s.Scaling;
                var waBottom = waTop + wa.Height / s.Scaling;

                if (left + 100 > waLeft && left < waRight - 50 &&
                    top + 50 > waTop && top < waBottom - 50)
                {
                    isVisible = true;
                    break;
                }
            }

            if (isVisible)
                Position = new PixelPoint((int)(left * scaling), (int)(top * scaling));
            else
            {
                var cx = workArea.X + (workArea.Width - (int)(w * scaling)) / 2;
                var cy = workArea.Y + (workArea.Height - (int)(h * scaling)) / 2;
                Position = new PixelPoint(cx, cy);
            }
        }
        else
        {
            var cx = workArea.X + (workArea.Width - (int)(w * scaling)) / 2;
            var cy = workArea.Y + (workArea.Height - (int)(h * scaling)) / 2;
            Position = new PixelPoint(cx, cy);
        }

        if (settings.IsMaximized)
            WindowState = WindowState.Maximized;
    }

    private void CaptureBoundsToSettings()
    {
        if (DataContext is not MainViewModel vm) return;
        var settings = vm.DataStore.Data.Settings;

        settings.IsMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;

            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var scaling = screen?.Scaling ?? 1.0;
            settings.WindowLeft = Position.X / scaling;
            settings.WindowTop = Position.Y / scaling;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (DataContext is not MainViewModel vm) return;

        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;

        // Ctrl+N — new chat
        if (ctrl && e.Key == Key.N)
        {
            ShowPage(2); // Navigate to Chat tab
            vm.ChatVM.NewChat();
            e.Handled = true;
        }
    }
}
