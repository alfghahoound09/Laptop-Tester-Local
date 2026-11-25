using LaptopTester.App.ViewModels;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Services;
using LaptopTester.Tests.TestInfrastructure;
using Xunit;

namespace LaptopTester.Tests.AppLayer;

public sealed class MainViewModelTests
{
    private static (MainViewModel Main, TempDirectory Dir) CreateSut()
    {
        var dir = new TempDirectory();
        var loc = new LocalizationService();
        var store = new TestResultsStore();
        var bat = new BatteryService();
        var storage = new StorageHealthService();
        var hw = new HardwareService(bat, storage);
        var antiFake = new AntiFakeService(storage);
        var usb = new UsbConnectivityService();
        var audio = new AudioCameraService();
        var bench = new BenchmarkThermalService(storage);
        var audit = new SystemAuditService();
        var engine = new QuickTestEngine();
        var history = new HistoryReportService(dir.Path);

        var dashboardVm = new DashboardViewModel(hw, loc, store);
        var hardwareVm = new HardwareHubViewModel(hw, antiFake, bat, storage, bench, audit, store, loc);
        var devicesVm = new DeviceHubViewModel(usb, audio, bat, store, loc);
        var quickTestVm = new QuickTestViewModel(hw, antiFake, storage, bat, usb, engine, history, store, loc);
        var reportVm = new ReportViewModel(store, engine, hw, history, loc);
        var historyVm = new HistoryViewModel(history, loc);
        var settingsVm = new SettingsViewModel(loc, history);

        var main = new MainViewModel(loc, store, dashboardVm, quickTestVm, hardwareVm, devicesVm, reportVm, historyVm, settingsVm);
        return (main, dir);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void NavigationGoBackToggleNavPaneAndLabels_BehaveAsExpected()
    {
        var (main, dir) = CreateSut();
        using (dir)
        using (main.Devices)
        {
            Assert.IsType<DashboardViewModel>(main.CurrentViewModel);
            Assert.False(main.CanGoBack);

            main.Navigate(AppTab.QuickTest);
            Assert.IsType<QuickTestViewModel>(main.CurrentViewModel);
            Assert.True(main.CanGoBack);

            main.Navigate(AppTab.Hardware);
            Assert.IsType<HardwareHubViewModel>(main.CurrentViewModel);

            main.Navigate(AppTab.Devices);
            Assert.IsType<DeviceHubViewModel>(main.CurrentViewModel);

            main.Navigate(AppTab.Report);
            Assert.IsType<ReportViewModel>(main.CurrentViewModel);

            main.Navigate(AppTab.History);
            Assert.IsType<HistoryViewModel>(main.CurrentViewModel);

            main.Navigate(AppTab.Settings);
            Assert.IsType<SettingsViewModel>(main.CurrentViewModel);

            // GoBack from Devices non-Hub returns to Hub first, then to Dashboard
            main.Navigate(AppTab.Devices);
            main.Devices.SelectSection("Keyboard");
            Assert.Equal("Keyboard", main.Devices.ActiveSection);
            main.GoBackCommand.Execute(null);
            Assert.Equal(AppTab.Devices, main.ActiveTab);
            Assert.Equal("Hub", main.Devices.ActiveSection);
            main.GoBackCommand.Execute(null);
            Assert.Equal(AppTab.Dashboard, main.ActiveTab);

            // Toggle navigation rail width
            Assert.Equal(224, main.NavPaneWidth);
            main.ToggleNavPaneCommand.Execute(null);
            Assert.Equal(64, main.NavPaneWidth);
            main.ToggleNavPaneCommand.Execute(null);
            Assert.Equal(224, main.NavPaneWidth);

            // English-only navigation labels
            Assert.Equal("Dashboard", main.NavDashboard);
            Assert.Equal("Quick Test", main.NavQuickTest);
            Assert.Equal("Hardware", main.NavHardware);
            Assert.Equal("Settings", main.NavSettings);
            Assert.Contains("checks", main.SessionChecksBadge);
        }
    }
}
