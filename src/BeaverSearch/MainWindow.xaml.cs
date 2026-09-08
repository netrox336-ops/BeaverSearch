using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BeaverSearch.ViewModels;
using BeaverSearch.Infrastructure;

namespace BeaverSearch;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private bool _closing;
    private bool _initialized;

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        MainTabs.SelectedIndex = 0;
        NavMonitor.IsChecked = true;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        SourceInitialized += MainWindow_SourceInitialized;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        // Windows 11 can draw a bright 1px DWM border even for WindowStyle=None.
        // Explicitly remove it. Unsupported attributes on older Windows are simply ignored.
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var dark = 1;
            var noBorder = DwmColorNone;
            var caption = 0x00100E0D; // COLORREF for #0D0E10
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref noBorder, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        }
        catch
        {
            // DWM theming is cosmetic only; never fail startup because of it.
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Materialize the initial Monitor page before the first visible frame.
        MainTabs.SelectedIndex = 0;
        MainTabs.UpdateLayout();
        // Soft entrance applied to the content shell (Window itself cannot be transformed in WPF).
        var transform = new TranslateTransform(0, 7);
        RootShell.RenderTransform = transform;
        RootShell.RenderTransformOrigin = new Point(0.5, 0.5);
        RootShell.Opacity = 0.94;
        RootShell.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        if (_initialized) return;
        _initialized = true;
        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            App.ReportFatalStartupException("MainWindow initialization", ex);
        }
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton button && int.TryParse(button.CommandParameter?.ToString(), out var index))
            NavigateTo(index);
    }

    public void NavigateTo(int index)
    {
        if (index < 0 || index >= MainTabs.Items.Count) return;
        MainTabs.SelectedIndex = index;
        switch (index)
        {
            case 0: NavMonitor.IsChecked = true; break;
            case 1: NavServers.IsChecked = true; break;
            case 2: NavResults.IsChecked = true; break;
            case 3: NavDiagnostics.IsChecked = true; break;
            case 4: NavSettings.IsChecked = true; break;
        }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.Source != MainTabs) return;
        var transform = new TranslateTransform(12, 0);
        MainTabs.RenderTransform = transform;
        MainTabs.Opacity = 0.72;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        MainTabs.BeginAnimation(OpacityProperty, new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(225)) { EasingFunction = ease });
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        // Global fallback: WPF controls such as DataGrid can swallow the wheel
        // before the page sees it. Scroll the outer page instead.
        if (MainTabs.SelectedContent is not DependencyObject selected) return;
        var viewer = FindPageScrollViewer(selected);
        if (viewer is not null) PageScrollHelper.Handle(viewer, e);
    }

    private static ScrollViewer? FindPageScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer && viewer.Name == "PageScroll") return viewer;
            var nested = FindPageScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == System.Windows.WindowState.Maximized
            ? System.Windows.WindowState.Normal
            : System.Windows.WindowState.Maximized;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        await _vm.ShutdownAsync();
        Closing -= MainWindow_Closing;
        Close();
    }
}
