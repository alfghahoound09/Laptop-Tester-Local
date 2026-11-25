using LaptopTester.App.ViewModels;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;
using LaptopTester.Tests.TestInfrastructure;
using Xunit;

namespace LaptopTester.Tests.AppLayer;

public sealed class QuickTestViewModelTests
{
    private static (QuickTestViewModel Vm, TestResultsStore Store, LocalizationService Loc, TempDirectory Dir) CreateSut()
    {
        var dir = new TempDirectory();
        var storage = new StorageHealthService();
        var battery = new BatteryService();
        var hw = new HardwareService(battery, storage);
        var antiFake = new AntiFakeService(storage);
        var usb = new UsbConnectivityService();
        var engine = new QuickTestEngine();
        var history = new HistoryReportService(dir.Path);
        var store = new TestResultsStore();
        var loc = new LocalizationService();

        var vm = new QuickTestViewModel(hw, antiFake, storage, battery, usb, engine, history, store, loc);
        return (vm, store, loc, dir);
    }

    [Fact]
    public void StepsAndTargets_AreInitializedCorrectly()
    {
        var (vm, _, _, dir) = CreateSut();
        using (dir)
        {
            Assert.Equal(10, vm.Steps.Count);
            Assert.Null(vm.Steps.First(s => s.Id == "hardware").DeviceTarget);
            Assert.Null(vm.Steps.First(s => s.Id == "storage").DeviceTarget);
            Assert.Null(vm.Steps.First(s => s.Id == "battery").DeviceTarget);
            Assert.Equal("Display", vm.Steps.First(s => s.Id == "display").DeviceTarget);
            Assert.Equal("Keyboard", vm.Steps.First(s => s.Id == "keyboard").DeviceTarget);
            Assert.Equal("Touchpad", vm.Steps.First(s => s.Id == "touchpad").DeviceTarget);
            Assert.Equal("Audio", vm.Steps.First(s => s.Id == "speaker").DeviceTarget);
            Assert.Equal("Audio", vm.Steps.First(s => s.Id == "microphone").DeviceTarget);
            Assert.Equal("Camera", vm.Steps.First(s => s.Id == "camera").DeviceTarget);
            Assert.Equal("Connectivity", vm.Steps.First(s => s.Id == "connectivity").DeviceTarget);
        }
    }

    [Fact]
    public void SetStepStatusLanguageSwitchAndNavigation_WorkTogether()
    {
        var (vm, store, loc, dir) = CreateSut();
        using (dir)
        {
            vm.SetStepStatusCommand.Execute("display|Fail");
            Assert.Equal(TestStatus.Failed, store.Get("display")?.Status);
            Assert.Equal(TestStatus.Failed, vm.Steps.First(s => s.Id == "display").Status);
            Assert.Equal(88, vm.CurrentScore);
            Assert.Equal(VerdictBand.DoNotBuy, vm.VerdictBand);
            Assert.Equal(1, vm.CheckedCount);

            vm.SetStepStatusCommand.Execute("keyboard|Skip");
            Assert.Equal(TestStatus.Skipped, vm.Steps.First(s => s.Id == "keyboard").Status);
            Assert.Equal(88, vm.CurrentScore);

            Assert.Equal("Hardware & Authenticity Check", vm.Steps[0].Title);
            Assert.Equal(TestStatus.Failed, vm.Steps.First(s => s.Id == "display").Status);
            Assert.Equal(TestStatus.Skipped, vm.Steps.First(s => s.Id == "keyboard").Status);

            string? navTarget = null;
            vm.NavigateToDeviceRequested += t => navTarget = t;
            vm.OpenDeviceTestCommand.Execute(vm.Steps.First(s => s.Id == "keyboard"));
            Assert.Equal("Keyboard", navTarget);
        }
    }
}
