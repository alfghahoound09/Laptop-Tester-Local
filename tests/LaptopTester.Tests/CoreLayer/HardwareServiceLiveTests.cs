using LaptopTester.Core.Services;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class HardwareServiceLiveTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task ScanHardwareAsync_PopulatesCpuRamGpuDrivesAndBattery()
    {
        var svc = new HardwareService(new BatteryService(), new StorageHealthService());
        var report = await svc.ScanHardwareAsync();

        Assert.False(string.IsNullOrWhiteSpace(report.Cpu.Name));
        Assert.NotEqual("Unknown CPU", report.Cpu.Name);
        Assert.True(report.Cpu.Cores > 0);
        Assert.True(report.Cpu.Threads >= report.Cpu.Cores);
        Assert.True(report.Cpu.MaxClockMhz > 0);

        Assert.True(report.Ram.TotalInstalledGb > 0);
        Assert.True(report.Ram.UsableGb > 0);
        Assert.True(report.Ram.UsableGb <= report.Ram.TotalInstalledGb + 0.5);

        Assert.NotEmpty(report.Gpus);
        Assert.All(report.Gpus, g =>
            Assert.Contains(g.AdapterType, new[] { GpuKind.Discrete, GpuKind.Integrated, GpuKind.Virtual }));
        Assert.False(string.IsNullOrWhiteSpace(report.Summary.GpuSummary));

        bool hasDgpu = report.Gpus.Any(g => g.AdapterType == GpuKind.Discrete);
        bool hasIgpu = report.Gpus.Any(g => g.AdapterType == GpuKind.Integrated);
        if (hasDgpu && hasIgpu)
        {
            Assert.Equal(GpuKind.Discrete, HardwareService.SelectPrimaryGpu(report.Gpus)?.AdapterType);
            Assert.Contains("(dGPU)", report.Summary.GpuSummary);
            Assert.Contains("(iGPU)", report.Summary.GpuSummary);
            Assert.True(report.Summary.GpuSummary.IndexOf("(dGPU)", StringComparison.Ordinal) <
                        report.Summary.GpuSummary.IndexOf("(iGPU)", StringComparison.Ordinal));
        }

        Assert.NotEmpty(report.Drives);
        Assert.InRange(report.Battery.HealthPercent, 0, 120);
        Assert.False(string.IsNullOrWhiteSpace(report.Summary.Manufacturer));
        Assert.False(string.IsNullOrWhiteSpace(report.Summary.Model));
    }

    [SkippableFact]
    [Trait("Category", "Hardware")]
    public void EnumerateDisplays_ReturnsValidGeometryAndSingleOrZeroPrimary()
    {
        var displays = HardwareService.EnumerateDisplays();
        Skip.If(displays.Count == 0, "No attached displays detected in headless environment.");

        Assert.All(displays, d =>
        {
            Assert.True(d.WidthPx > 0);
            Assert.True(d.HeightPx > 0);
            Assert.True(d.RefreshRateHz > 0);
        });
        Assert.True(displays.Count(d => d.IsPrimary) <= 1);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void IsCurrentProcessAdministrator_DoesNotThrow()
    {
        _ = HardwareService.IsCurrentProcessAdministrator();
    }
}
