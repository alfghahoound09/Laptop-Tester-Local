using LaptopTester.Core.Services;
using LaptopTester.Tests.TestInfrastructure;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

[Collection("Heavy")]
public sealed class BatteryTests
{
    [Theory]
    [InlineData((byte)1, (byte)8, (byte)80, -1, false, true, true, 80, 0)]
    [InlineData((byte)0, (byte)0, (byte)55, 7200, false, false, false, 55, 120)]
    [InlineData((byte)255, (byte)128, (byte)255, -1, true, false, false, 0, 0)]
    public void DecodePowerStatus_DecodesFlagsPercentAndMinutes(
        byte ac, byte flag, byte pct, int lifeSec,
        bool expNoBat, bool expAc, bool expCharging, int expPct, int expMin)
    {
        var decoded = BatteryService.DecodePowerStatus(ac, flag, pct, lifeSec);
        Assert.Equal(expNoBat, decoded.NoSystemBattery);
        Assert.Equal(expAc, decoded.IsAc);
        Assert.Equal(expCharging, decoded.IsCharging);
        Assert.Equal(expPct, decoded.Percent);
        Assert.Equal(expMin, decoded.RemainingMinutes);
    }

    [Theory]
    [InlineData(60000, 50000, 83.3)]
    [InlineData(50000, 70000, 120.0)]
    [InlineData(0, 50000, 0.0)]
    [InlineData(60000, 0, 0.0)]
    public void ComputeHealthPercent_HandlesClampAndZeroGuards(int designed, int full, double expected)
    {
        Assert.Equal(expected, BatteryService.ComputeHealthPercent(designed, full));
    }

    [Fact]
    public void EstimateRemainingMinutes_ComputesFromWattsOrPercentDrop()
    {
        Assert.Equal(120.0, BatteryService.EstimateRemainingMinutes(10.0, 20000, 10, 50, 0), 3);
        Assert.Equal(25.0, BatteryService.EstimateRemainingMinutes(0.2, 0, 60, 50, 2), 3);
        Assert.Equal(0.0, BatteryService.EstimateRemainingMinutes(0.2, 0, 60, 50, 0), 3);
    }

    [Theory]
    [InlineData(3, "Lead Acid")]
    [InlineData(4, "NiCd")]
    [InlineData(5, "NiMH")]
    [InlineData(6, "Li-Ion")]
    [InlineData(7, "Zinc-Air")]
    [InlineData(8, "Li-Poly")]
    [InlineData(99, "Li-Ion / Polymer")]
    public void DecodeChemistry_MapsKnownCodes(int code, string expected)
    {
        Assert.Equal(expected, BatteryService.DecodeChemistry(code));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task GetBatteryInfoAsync_ReturnsValidTelemetry()
    {
        var svc = new BatteryService();
        var info = await svc.GetBatteryInfoAsync();

        if (info.HasBattery)
        {
            Assert.InRange(info.ChargePercent, 0, 100);
            Assert.True(info.VoltageVolts >= 0);
        }
        else
        {
            Assert.Equal(0, info.ChargePercent);
        }
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task RunStressTestAsync_ReportsSequentialProgressAndSupportsCancellation()
    {
        var svc = new BatteryService();
        var progress = new SyncProgress<BatteryStressProgress>();
        await svc.RunStressTestAsync(2, progress, CancellationToken.None);

        var items = progress.Snapshot();
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].ElapsedSeconds);
        Assert.Equal(2, items[0].TotalSeconds);
        Assert.Equal(2, items[1].ElapsedSeconds);
        Assert.Equal(2, items[1].TotalSeconds);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.RunStressTestAsync(30, new SyncProgress<BatteryStressProgress>(), cts.Token));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }
}
