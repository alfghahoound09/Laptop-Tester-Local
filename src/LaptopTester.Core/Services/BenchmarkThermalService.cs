using System.Diagnostics;
using System.Runtime.InteropServices;
using LaptopTester.Core.Models;

namespace LaptopTester.Core.Services;

/// <summary>Runs real CPU and memory bandwidth benchmarks and captures thermal/load snapshots.</summary>
public sealed class BenchmarkThermalService
{
    private readonly StorageHealthService _storageService;

    public BenchmarkThermalService(StorageHealthService storageService)
    {
        _storageService = storageService;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    public async Task<TemperatureSnapshot> CaptureThermalSnapshotAsync(CancellationToken ct = default)
    {
        var acpiTask = WmiQueryHelper.QueryAsync("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", @"root\wmi", ct);
        var perfTask = WmiQueryHelper.QueryAsync("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'", ct: ct);
        var drivesTask = _storageService.GetDrivesAsync(ct);

        await Task.WhenAll(acpiTask, perfTask, drivesTask);

        double? cpuC = null;
        var temps = acpiTask.Result
            .Select(r => (WmiQueryHelper.Int(r, "CurrentTemperature") - 2732) / 10.0)
            .Where(t => t > 5 && t < 130)
            .ToList();
        if (temps.Count > 0)
            cpuC = Math.Round(temps.Max(), 1);

        double? maxDriveC = drivesTask.Result.Where(d => d.TemperatureC.HasValue).Select(d => (double?)d.TemperatureC!.Value).Max();
        double cpuLoad = perfTask.Result.FirstOrDefault() is { } pRow ? WmiQueryHelper.Int(pRow, "PercentProcessorTime") : 0;

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        double ramUsed = GlobalMemoryStatusEx(ref mem) ? mem.dwMemoryLoad : 0;

        string source = cpuC.HasValue
            ? "ACPI Thermal Zone + Storage SMART"
            : HardwareService.IsCurrentProcessAdministrator()
                ? "ACPI sensor unavailable on this board"
                : "CPU thermal sensor requires Administrator on this board";

        return new TemperatureSnapshot(cpuC, maxDriveC, cpuLoad, ramUsed, source, DateTimeOffset.Now);
    }

    public async Task<BenchmarkReport> RunBenchmarkAsync(bool includeDisk, IProgress<string>? status = null, CancellationToken ct = default)
    {
        status?.Report("Running single-core CPU test...");
        double singleScore = await Task.Run(() => RunCpuPass(threads: 1, limit: 220_000, ct), ct);

        status?.Report("Running multi-core CPU test...");
        var swMulti = Stopwatch.StartNew();
        double multiScore = await Task.Run(() => RunCpuPass(threads: Environment.ProcessorCount, limit: 350_000, ct), ct);
        swMulti.Stop();

        status?.Report("Running RAM memory bandwidth test...");
        double memGbSec = await Task.Run(() => RunMemoryBandwidthPass(ct), ct);

        double? diskRead = null;
        double? diskWrite = null;
        if (includeDisk)
        {
            status?.Report("Running sequential storage benchmark...");
            var (r, w) = await _storageService.RunSpeedTestAsync(sizeMb: 96, ct: ct);
            diskRead = r;
            diskWrite = w;
        }

        string tier = multiScore >= 12000
            ? "High Performance / Workstation"
            : multiScore >= 6000
                ? "Mainstream / Balanced"
                : "Entry / Ultralight";

        return new BenchmarkReport(
            Math.Round(singleScore, 0),
            Math.Round(multiScore, 0),
            Math.Round(swMulti.Elapsed.TotalMilliseconds, 0),
            Math.Round(memGbSec, 2),
            diskRead,
            diskWrite,
            tier,
            DateTimeOffset.Now);
    }

    private static double RunCpuPass(int threads, int limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long totalOps = 0;
        Parallel.For(0, threads, new ParallelOptions { CancellationToken = ct }, _ =>
        {
            long count = 0;
            for (int n = 3; n < limit; n += 2)
            {
                if ((n & 1023) == 1) ct.ThrowIfCancellationRequested();
                bool prime = true;
                for (int d = 3; d * d <= n; d += 2)
                {
                    if (n % d == 0) { prime = false; break; }
                }
                if (prime) count++;
            }
            Interlocked.Add(ref totalOps, count);
        });
        sw.Stop();
        return totalOps / Math.Max(0.05, sw.Elapsed.TotalSeconds) / 100.0;
    }

    private static double RunMemoryBandwidthPass(CancellationToken ct)
    {
        const int sizeBytes = 64 * 1024 * 1024; // 64 MB
        byte[] src = new byte[sizeBytes];
        byte[] dst = new byte[sizeBytes];
        Array.Fill(src, (byte)0x5A);
        Buffer.BlockCopy(src, 0, dst, 0, sizeBytes); // warm-up

        const int iterations = 12;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            Buffer.BlockCopy(src, 0, dst, 0, sizeBytes);
        }
        sw.Stop();

        double totalGb = (sizeBytes * (double)iterations) / (1024.0 * 1024.0 * 1024.0);
        return totalGb / Math.Max(0.01, sw.Elapsed.TotalSeconds);
    }
}
