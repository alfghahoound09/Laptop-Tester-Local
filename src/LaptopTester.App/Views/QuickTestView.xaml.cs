using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LaptopTester.App.ViewModels;

namespace LaptopTester.App.Views;

public partial class QuickTestView : UserControl
{
    private Point? _lastTouchPos;
    private double _movedDistance;

    public QuickTestView() => InitializeComponent();

    private void QuickTouchPad_MouseMove(object sender, MouseEventArgs e)
    {
        if (DataContext is not QuickTestViewModel vm) return;
        var pos = e.GetPosition((IInputElement)sender);
        if (_lastTouchPos is { } prev)
        {
            _movedDistance += (pos - prev).Length;
            if (_movedDistance >= 35)
                vm.RecordQuickTouchpadActivity(moved: true, clicked: false, scrolled: false);
        }
        _lastTouchPos = pos;
    }

    private void QuickTouchPad_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is QuickTestViewModel vm)
            vm.RecordQuickTouchpadActivity(moved: false, clicked: true, scrolled: false);
    }

    private void QuickTouchPad_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is QuickTestViewModel vm)
            vm.RecordQuickTouchpadActivity(moved: false, clicked: false, scrolled: true);
        e.Handled = true;
    }
}
