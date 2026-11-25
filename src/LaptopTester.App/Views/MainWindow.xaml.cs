using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LaptopTester.App.ViewModels;

namespace LaptopTester.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private bool _lastKeyExtended;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        StateChanged += OnWindowStateChanged;

        vm.Devices.LaunchDisplayFullScreenRequested += mode =>
        {
            var win = new FullScreenTestWindow(mode) { Owner = this };
            win.ShowDialog();
        };

        vm.QuickTest.LaunchExpressDisplayRequested += onVerdict =>
        {
            var win = new FullScreenTestWindow("Solid", expressConfirmMode: true) { Owner = this };
            win.ShowDialog();
            onVerdict(win.ExpressVerdict);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));

            int corner = 2; // DWMWCP_ROUND (Windows 11 rounded corners)
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref corner, sizeof(int));

            int borderColor = 0x00302626; // COLORREF BGR for #262630
            DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref borderColor, sizeof(int));

            HwndSource.FromHwnd(hwnd)?.AddHook(OnWindowMessage);
        }
        catch { }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
            _lastKeyExtended = ((lParam.ToInt64() >> 24) & 1) != 0;
        return IntPtr.Zero;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        OuterWindowBorder.Margin = maximized ? new Thickness(7) : new Thickness(0);
        OuterWindowBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

    private bool IsKeyboardTestActive =>
        _vm.ActiveTab == AppTab.Devices && _vm.Devices.ActiveSection == "Keyboard";

    private bool IsQuickKeyboardActive =>
        _vm.ActiveTab == AppTab.QuickTest
        && _vm.QuickTest.ActiveInteractiveStepId.Equals("keyboard", StringComparison.OrdinalIgnoreCase);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsKeyboardTestActive)
        {
            Key actual = e.Key == Key.System ? e.SystemKey : e.Key;
            _vm.Devices.RecordKeyEvent(actual.ToString(), true, _lastKeyExtended);
            e.Handled = true;
            return;
        }

        if (IsQuickKeyboardActive)
        {
            Key actual = e.Key == Key.System ? e.SystemKey : e.Key;
            if (actual is not (Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6 or Key.F7))
            {
                _vm.QuickTest.RecordQuickKeyPress(actual.ToString());
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.F1: _vm.Navigate(AppTab.Dashboard); e.Handled = true; break;
            case Key.F2: _vm.Navigate(AppTab.QuickTest); e.Handled = true; break;
            case Key.F3: _vm.Navigate(AppTab.Hardware); e.Handled = true; break;
            case Key.F4: _vm.Navigate(AppTab.Devices); e.Handled = true; break;
            case Key.F5: _vm.Navigate(AppTab.Report); e.Handled = true; break;
            case Key.F6: _vm.Navigate(AppTab.History); e.Handled = true; break;
            case Key.F7: _vm.Navigate(AppTab.Settings); e.Handled = true; break;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsKeyboardTestActive) return;
        Key actual = e.Key == Key.System ? e.SystemKey : e.Key;
        _vm.Devices.RecordKeyEvent(actual.ToString(), false, _lastKeyExtended);
        e.Handled = true;
    }
}
