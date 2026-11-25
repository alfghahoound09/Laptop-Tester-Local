using LaptopTester.Core.Services;
using LaptopTester.Tests.TestInfrastructure;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

[Collection("Heavy")]
public sealed class BenchmarkThermalTests
{
    [Theory]
    [InlineData(11999.9, "Mainstream / Balanced")]
    [InlineData(12000.0, "High Performance / Workstation")]
    [InlineData(5999.9, "Entry / Ultralight")]
    [InlineData(6000.0, "Mainstream / Balanced")]
    public void ClassifyTier_RespectsBoundaries(double multiScore, string expected)
    {
        Assert.Equal(expected, BenchmarkThermalService.ClassifyTier(multiScore));
    }

    [Fact]
    public void AcpiToCelsius_ConvertsValidRangeAndFiltersOutOfRange()
    {
        Assert.Equal(30.0, BenchmarkThermalService.AcpiToCelsius(3032));
        Assert.Null(BenchmarkThermalService.AcpiToCelsius(2732)); // 0°C -> excluded
        Assert.Null(BenchmarkThermalService.AcpiToCelsius(4500)); // 176.8°C -> excluded
    }

    [Fact]
    public void RunCpuPass_And_MemoryBandwidth_ProducePositiveResultsAndHonorCancellation()
    {
        double cpuScore = BenchmarkThermalService.RunCpuPass(1, 20_000, CancellationToken.None);
        Assert.True(cpuScore > 0);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            BenchmarkThermalService.RunCpuPass(1, 200_000, cancelled.Token));

        double memGbSec = BenchmarkThermalService.RunMemoryBandwidthPass(CancellationToken.None);
        Assert.True(memGbSec > 0);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task CaptureThermalSnapshotAsync_ReturnsValidTelemetryRanges()
    {
        var svc = new BenchmarkThermalService(new StorageHealthService());
        var snap = await svc.CaptureThermalSnapshotAsync();

        Assert.InRange(snap.CpuLoadPercent, 0, 100);
        Assert.InRange(snap.RamUsedPercent, 0, 100);
        if (snap.CpuPackageC.HasValue)
            Assert.InRange(snap.CpuPackageC.Value, 5.1, 129.9);
        Assert.False(string.IsNullOrWhiteSpace(snap.SourceNote));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task RunBenchmarkAsync_WithoutDisk_ReportsProgressAndScores()
    {
        var svc = new BenchmarkThermalService(new StorageHealthService());
        var progress = new SyncProgress<string>();
        var report = await svc.RunBenchmarkAsync(includeDisk: false, progress);

        Assert.True(report.CpuSingleScore > 0);
        Assert.True(report.CpuMultiScore > 0);
        Assert.True(report.MemoryBandwidthGbPerSec > 0);
        Assert.Null(report.DiskSeqReadMbPerSec);
        Assert.Null(report.DiskSeqWriteMbPerSec);
        Assert.False(string.IsNullOrWhiteSpace(report.OverallTier));
        Assert.True(progress.Snapshot().Count >= 3);
    }
}
