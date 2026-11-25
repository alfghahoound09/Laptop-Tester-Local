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
            Assert.Equal("N/A", info.CycleDisplay);
            Assert.Equal("No Battery", info.CycleSource);
        }
    }

    [Fact]
    public void SanitizeUint_FiltersSentinelsAndOutOfRangeValues()
    {
        Assert.Equal(0, BatteryService.SanitizeUint(0, 100_000));
        Assert.Equal(245, BatteryService.SanitizeUint(245, 100_000));
        Assert.Equal(0, BatteryService.SanitizeUint(0xFFFFFFFFuL, 100_000));
        Assert.Equal(0, BatteryService.SanitizeUint(250_000, 100_000));
    }

    [Fact]
    public void CorrelateWmiBatteries_AggregatesDualBatteriesAndFiltersSentinels()
    {
        var staticRows = new List<Dictionary<string, object?>>
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["InstanceName"] = @"ACPI\PNP0C0A\1",
                ["DesignedCapacity"] = 45_000u,
                ["ManufactureName"] = "SMP",
                ["SerialNumber"] = "BAT-01",
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["InstanceName"] = @"ACPI\PNP0C0A\2",
                ["DesignedCapacity"] = 24_000u,
                ["ManufactureName"] = "SMP",
                ["SerialNumber"] = "BAT-02",
            },
        };
        var fullRows = new List<Dictionary<string, object?>>
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["InstanceName"] = @"ACPI\PNP0C0A\1",
                ["FullChargedCapacity"] = 40_000u,
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["InstanceName"] = @"ACPI\PNP0C0A\2",
                ["FullChargedCapacity"] = 20_000u,
            },
        };
        var cycleRows = new List<Dictionary<string, object?>>
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["InstanceName"] = @"ACPI\PNP0C0A\1",
                ["CycleCount"] = 120u,
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["InstanceName"] = @"ACPI\PNP0C0A\2",
                ["CycleCount"] = 0xFFFFFFFFu,
            },
        };
        var statusRows = new List<Dictionary<string, object?>>
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["InstanceName"] = @"ACPI\PNP0C0A\1",
                ["RemainingCapacity"] = 30_000u,
                ["Voltage"] = 11_400u,
                ["DischargeRate"] = 12_000u,
                ["ChargeRate"] = 0u,
                ["Charging"] = false,
                ["PowerOnline"] = true,
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["InstanceName"] = @"ACPI\PNP0C0A\2",
                ["RemainingCapacity"] = 15_000u,
                ["Voltage"] = 11_200u,
                ["DischargeRate"] = 0u,
                ["ChargeRate"] = 5_000u,
                ["Charging"] = true,
                ["PowerOnline"] = false,
            },
        };

        var result = BatteryService.CorrelateWmiBatteries(staticRows, fullRows, cycleRows, statusRows);

        Assert.Equal(69_000, result.DesignedMwh);
        Assert.Equal(60_000, result.FullChargedMwh);
        Assert.Equal(45_000, result.RemainingMwh);
        Assert.Equal(120, result.CycleCount);
        Assert.Equal(12_000, result.TotalDischargeMw);
        Assert.Equal(5_000, result.TotalChargeMw);
        Assert.True(result.AnyCharging);
        Assert.True(result.AnyPowerOnline);
        Assert.Equal("SMP", result.Manufacturer);
        Assert.Contains("BAT-01", result.SerialNumber);
    }

    [Fact]
    public void ParseBatteryReportXml_ExtractsReportedCyclesAndMetadata()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
              <Batteries>
                <Battery>
                  <Manufacturer>LGC</Manufacturer>
                  <SerialNumber>4209</SerialNumber>
                  <ManufactureDate>2024-03-15</ManufactureDate>
                  <DesignCapacity>50000</DesignCapacity>
                  <FullChargeCapacity>44000</FullChargeCapacity>
                  <CycleCount>84</CycleCount>
                </Battery>
              </Batteries>
              <History>
                <HistoryEntry StartDate="2024-06-01" EndDate="2024-06-08" FullChargeCapacity="48000" DesignCapacity="50000" CycleCount="20" />
                <HistoryEntry StartDate="2025-01-01" EndDate="2025-01-08" FullChargeCapacity="44000" DesignCapacity="50000" CycleCount="84" />
              </History>
            </BatteryReport>
            """;

        var report = BatteryService.ParseBatteryReportXml(xml);

        Assert.NotNull(report);
        Assert.Equal(84, report.ReportedCycleCount);
        Assert.Equal(50_000, report.DesignedCapacityMwh);
        Assert.Equal(44_000, report.FullChargedCapacityMwh);
        Assert.Equal("LGC", report.Manufacturer);
        Assert.Equal("4209", report.SerialNumber);
        Assert.Equal("2024-03-15", report.ManufactureDate);
        Assert.Equal("OS History: 96% -> 88%", report.HistoryTrendNote);
    }

    [Fact]
    public void ParseBatteryReportXml_EstimatesCyclesWhenReportedCountMissing()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
              <Batteries>
                <Battery>
                  <Manufacturer>Celxpert</Manufacturer>
                  <DesignCapacity>50000</DesignCapacity>
                  <FullChargeCapacity>45000</FullChargeCapacity>
                  <CycleCount>-</CycleCount>
                </Battery>
              </Batteries>
              <History>
                <HistoryEntry StartDate="2025-01-01" EndDate="2025-01-08" FullChargeCapacity="47000" ActiveEnergyDrain="80000" ConnectedStandbyEnergyDrain="20000" />
                <HistoryEntry StartDate="2025-01-08" EndDate="2025-01-15" FullChargeCapacity="45000" ActiveEnergyDrain="45000" ConnectedStandbyEnergyDrain="5000" />
              </History>
            </BatteryReport>
            """;

        var report = BatteryService.ParseBatteryReportXml(xml);

        Assert.NotNull(report);
        Assert.Equal(0, report.ReportedCycleCount);
        Assert.Equal(3, report.EstimatedCycleCount);
        Assert.Equal("OS History: 94% -> 90%", report.HistoryTrendNote);
    }

    [Fact]
    public void ParseBatteryReportXml_HandlesMalformedInputGracefully()
    {
        Assert.Null(BatteryService.ParseBatteryReportXml(""));
        Assert.Null(BatteryService.ParseBatteryReportXml("<broken"));
    }

    [Fact]
    public void BuildLifecycleNote_DetectsConservationModeAndEstimatedCycles()
    {
        var acHeld = BatteryService.BuildLifecycleNote(
            hasBattery: true, isAc: true, isCharging: false, chargePercent: 80,
            rateWatts: 0.0, healthPercent: 92.0, isEstimatedCycles: false,
            historyTrend: null, cycleCount: 45);
        Assert.Contains("80%", acHeld);

        var estimated = BatteryService.BuildLifecycleNote(
            hasBattery: true, isAc: false, isCharging: false, chargePercent: 60,
            rateWatts: 14.5, healthPercent: 88.0, isEstimatedCycles: true,
            historyTrend: "OS History: 96% -> 88%", cycleCount: 30);
        Assert.Contains("estimated (~30)", estimated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OS History: 96% -> 88%", estimated);

        var desktop = BatteryService.BuildLifecycleNote(
            hasBattery: false, isAc: true, isCharging: false, chargePercent: 0,
            rateWatts: 0.0, healthPercent: 0.0, isEstimatedCycles: false,
            historyTrend: null, cycleCount: 0);
        Assert.Equal("No battery installed", desktop);
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
