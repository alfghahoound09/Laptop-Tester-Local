using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LaptopTester.App.ViewModels;

namespace LaptopTester.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.Devices.LaunchDisplayFullScreenRequested += mode =>
        {
            var win = new FullScreenTestWindow(mode) { Owner = this };
            win.ShowDialog();
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
        }
        catch { }
    }

    private bool IsKeyboardTestActive =>
        _vm.ActiveTab == AppTab.Devices && _vm.Devices.ActiveSection == "Keyboard";

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsKeyboardTestActive)
        {
            Key actual = e.Key == Key.System ? e.SystemKey : e.Key;
            _vm.Devices.RecordKeyEvent(actual.ToString(), true);
            e.Handled = true;
            return;
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
        _vm.Devices.RecordKeyEvent(actual.ToString(), false);
        e.Handled = true;
    }
}
