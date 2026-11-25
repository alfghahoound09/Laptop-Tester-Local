using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LaptopTester.App.ViewModels;

namespace LaptopTester.App.Views;

public partial class DeviceHubView : UserControl
{
    public DeviceHubView() => InitializeComponent();

    private void OnClearTouchpadClick(object sender, RoutedEventArgs e)
    {
        TouchpadCanvas.Strokes.Clear();
        if (DataContext is DeviceHubViewModel vm)
            vm.ResetTouchpadCommand.Execute(null);
    }

    private void OnTouchpadMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DeviceHubViewModel vm) return;
        if (e.ChangedButton == MouseButton.Left) vm.LeftClicks++;
        else if (e.ChangedButton == MouseButton.Right) vm.RightClicks++;
        else if (e.ChangedButton == MouseButton.Middle) vm.MiddleClicks++;
    }

    private void OnTouchpadMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is DeviceHubViewModel vm)
            vm.WheelEvents++;
    }
}
