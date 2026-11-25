using System.Runtime.InteropServices;
using LaptopTester.Core.Models;

namespace LaptopTester.Core.Services;

public sealed record BatteryStressProgress(
    int ElapsedSeconds,
    int TotalSeconds,
    int CurrentPercent,
    double DischargeWatts,
    double PercentDropped,
    double EstimatedTotalMinutes,
    string StatusText);

/// <summary>Reads Windows battery health/telemetry and runs controlled drain stress tests.</summary>
public sealed class BatteryService
{
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public async Task<BatteryDetails> GetBatteryInfoAsync(CancellationToken ct = default)
    {
        GetSystemPowerStatus(out var sps);
        bool noSystemBattery = (sps.BatteryFlag & 128) != 0;
        bool isAc = sps.ACLineStatus == 1;
        bool isCharging = (sps.BatteryFlag & 8) != 0;
        int pct = sps.BatteryLifePercent <= 100 ? sps.BatteryLifePercent : 0;
        int remMinutes = sps.BatteryLifeTime > 0 ? sps.BatteryLifeTime / 60 : 0;

        var wmiBatTask = WmiQueryHelper.QueryAsync("SELECT DesignedCapacity, FullChargeCapacity, Chemistry, DeviceID, Name FROM Win32_Battery", ct: ct);
        var staticTask = WmiQueryHelper.QueryAsync("SELECT DesignedCapacity, ManufactureName, SerialNumber, Chemistry FROM BatteryStaticData", @"root\wmi", ct);
        var fullTask = WmiQueryHelper.QueryAsync("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\wmi", ct);
        var cycleTask = WmiQueryHelper.QueryAsync("SELECT CycleCount FROM BatteryCycleCount", @"root\wmi", ct);
        var statusTask = WmiQueryHelper.QueryAsync("SELECT Voltage, DischargeRate, ChargeRate, RemainingCapacity, Charging, Discharging, PowerOnline FROM BatteryStatus", @"root\wmi", ct);

        await Task.WhenAll(wmiBatTask, staticTask, fullTask, cycleTask, statusTask);

        var wmiBat = wmiBatTask.Result.FirstOrDefault();
        var staticRow = staticTask.Result.FirstOrDefault();
        var fullRow = fullTask.Result.FirstOrDefault();
        var cycleRow = cycleTask.Result.FirstOrDefault();
        var statusRow = statusTask.Result.FirstOrDefault();

        bool hasBattery = !noSystemBattery && (wmiBat is not null || staticRow is not null || sps.BatteryLifePercent <= 100);
        if (!hasBattery)
        {
            return new BatteryDetails(false, isAc, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, "N/A", "N/A", "N/A");
        }

        int designed = staticRow is not null ? WmiQueryHelper.Int(staticRow, "DesignedCapacity") : 0;
        if (designed <= 0 && wmiBat is not null)
            designed = WmiQueryHelper.Int(wmiBat, "DesignedCapacity");

        int full = fullRow is not null ? WmiQueryHelper.Int(fullRow, "FullChargedCapacity") : 0;
        if (full <= 0 && wmiBat is not null)
            full = WmiQueryHelper.Int(wmiBat, "FullChargeCapacity");

        var smbios = SmbiosReader.Read();
        if (designed <= 0 && smbios.BatteryDesignCapacityMwh > 0)
            designed = smbios.BatteryDesignCapacityMwh;

        int remaining = statusRow is not null ? WmiQueryHelper.Int(statusRow, "RemainingCapacity") : 0;
        int cycles = cycleRow is not null ? WmiQueryHelper.Int(cycleRow, "CycleCount") : 0;
        int voltageMv = statusRow is not null ? WmiQueryHelper.Int(statusRow, "Voltage") : 0;
        int dischargeMw = statusRow is not null ? WmiQueryHelper.Int(statusRow, "DischargeRate") : 0;
        int chargeMw = statusRow is not null ? WmiQueryHelper.Int(statusRow, "ChargeRate") : 0;

        if (statusRow is not null)
        {
            isCharging = isCharging || WmiQueryHelper.Bool(statusRow, "Charging");
            isAc = isAc || WmiQueryHelper.Bool(statusRow, "PowerOnline");
        }

        double health = designed > 0 && full > 0 ? Math.Round(Math.Clamp(full * 100.0 / designed, 0, 120), 1) : 0;
        double rateWatts = Math.Round((isCharging ? chargeMw : dischargeMw) / 1000.0, 2);
        double volts = Math.Round(voltageMv / 1000.0, 2);

        string mfg = staticRow is not null ? WmiQueryHelper.Str(staticRow, "ManufactureName", smbios.BatteryManufacturer ?? "Unknown") : (smbios.BatteryManufacturer ?? "Unknown");
        string serial = staticRow is not null ? WmiQueryHelper.Str(staticRow, "SerialNumber", "N/A") : "N/A";
        string chem = DecodeChemistry(wmiBat is not null ? WmiQueryHelper.Int(wmiBat, "Chemistry") : 0);

        return new BatteryDetails(
            true, isAc, isCharging, pct, remMinutes,
            designed, full, remaining, health, cycles,
            volts, rateWatts, mfg, chem, serial);
    }

    /// <summary>Runs a multi-core CPU workload for the requested duration and streams battery drain telemetry.</summary>
    public async Task RunStressTestAsync(
        int durationSeconds,
        IProgress<BatteryStressProgress> progress,
        CancellationToken ct)
    {
        var initial = await GetBatteryInfoAsync(ct);
        int startPct = initial.ChargePercent;
        int workerCount = Math.Max(1, Environment.ProcessorCount / 2);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() =>
        {
            double x = 1.000123;
            while (!linkedCts.Token.IsCancellationRequested)
            {
                for (int i = 0; i < 200_000; i++)
                    x = Math.Sqrt(x * 1.000001 + 3.14159);
                if (x > 1e12) x = 1.000123;
            }
        }, CancellationToken.None)).ToArray();

        try
        {
            for (int sec = 1; sec <= durationSeconds; sec++)
            {
                await Task.Delay(1000, ct);
                var snap = await GetBatteryInfoAsync(ct);
                int dropped = Math.Max(0, startPct - snap.ChargePercent);
                double estMinutes = snap.RateWatts > 0.5 && snap.RemainingCapacityMwh > 0
                    ? (snap.RemainingCapacityMwh / 1000.0) / snap.RateWatts * 60.0
                    : dropped > 0 ? sec / 60.0 * (snap.ChargePercent / (double)dropped) : 0;

                string status = snap.IsOnAcPower
                    ? "AC adapter plugged in — unplug to measure real drain"
                    : $"Load running ({workerCount} threads) · {snap.RateWatts:0.0}W";

                progress.Report(new BatteryStressProgress(
                    sec, durationSeconds, snap.ChargePercent,
                    snap.RateWatts, dropped, Math.Round(estMinutes, 0), status));
            }
        }
        finally
        {
            linkedCts.Cancel();
            try { await Task.WhenAll(workers); } catch { }
        }
    }

    private static string DecodeChemistry(int code) => code switch
    {
        3 => "Lead Acid",
        4 => "NiCd",
        5 => "NiMH",
        6 => "Li-Ion",
        7 => "Zinc-Air",
        8 => "Li-Poly",
        _ => "Li-Ion / Polymer"
    };
}
