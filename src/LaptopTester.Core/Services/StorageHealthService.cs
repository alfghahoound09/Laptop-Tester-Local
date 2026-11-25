using System.Diagnostics;
using System.IO;
using LaptopTester.Core.Models;

namespace LaptopTester.Core.Services;

/// <summary>Inspects physical disks, bus/media types, SMART failure prediction, and runs sequential speed tests.</summary>
public sealed class StorageHealthService
{
    public async Task<IReadOnlyList<StorageDriveDetails>> GetDrivesAsync(CancellationToken ct = default)
    {
        var diskTask = WmiQueryHelper.QueryAsync(
            "SELECT Index, Model, InterfaceType, MediaType, Size, SerialNumber FROM Win32_DiskDrive",
            ct: ct);

        var msftDiskTask = WmiQueryHelper.QueryAsync(
            "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, OperationalStatus, Size FROM MSFT_PhysicalDisk",
            @"root\Microsoft\Windows\Storage",
            ct);

        var smartTask = WmiQueryHelper.QueryAsync(
            "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus",
            @"root\wmi",
            ct);

        var reliabilityTask = WmiQueryHelper.QueryAsync(
            "SELECT DeviceId, Temperature, Wear FROM MSFT_StorageReliabilityCounter",
            @"root\Microsoft\Windows\Storage",
            ct);

        await Task.WhenAll(diskTask, msftDiskTask, smartTask, reliabilityTask);

        var msftById = msftDiskTask.Result
            .GroupBy(r => WmiQueryHelper.Str(r, "DeviceId"))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var relById = reliabilityTask.Result
            .GroupBy(r => WmiQueryHelper.Str(r, "DeviceId"))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var smartList = smartTask.Result;
        var drives = new List<StorageDriveDetails>();

        foreach (var row in diskTask.Result.OrderBy(r => WmiQueryHelper.Int(r, "Index")))
        {
            int idx = WmiQueryHelper.Int(row, "Index");
            string model = WmiQueryHelper.Str(row, "Model", $"Disk {idx}");
            string wmiIface = WmiQueryHelper.Str(row, "InterfaceType", "Unknown");
            string wmiMedia = WmiQueryHelper.Str(row, "MediaType", "");
            double sizeGb = Math.Round(WmiQueryHelper.ULong(row, "Size") / (1000.0 * 1000.0 * 1000.0), 1);
            string serial = WmiQueryHelper.Str(row, "SerialNumber", "N/A");

            msftById.TryGetValue(idx.ToString(), out var msftRow);
            relById.TryGetValue(idx.ToString(), out var relRow);

            int busType = msftRow is not null ? WmiQueryHelper.Int(msftRow, "BusType") : 0;
            int msftMedia = msftRow is not null ? WmiQueryHelper.Int(msftRow, "MediaType") : 0;
            int healthCode = msftRow is not null ? WmiQueryHelper.Int(msftRow, "HealthStatus") : -1;

            bool isExternal = IsExternalDrive(busType, wmiIface, wmiMedia);
            string ifaceLabel = MapBusType(busType, model, wmiIface);
            string mediaCategory = MapMediaCategory(msftMedia, busType, model);
            string healthStatus = MapHealthStatus(healthCode);

            int? tempC = relRow is not null && WmiQueryHelper.Int(relRow, "Temperature") is > 0 and < 150
                ? WmiQueryHelper.Int(relRow, "Temperature")
                : null;
            int? wear = relRow is not null && relRow.TryGetValue("Wear", out var wVal) && wVal is not null
                ? WmiQueryHelper.Int(relRow, "Wear")
                : null;

            bool? smartFail = null;
            if (idx < smartList.Count)
                smartFail = WmiQueryHelper.Bool(smartList[idx], "PredictFailure");

            drives.Add(new StorageDriveDetails(
                idx, model, ifaceLabel, mediaCategory, sizeGb, isExternal,
                serial, healthStatus, tempC, wear, smartFail, null, null));
        }

        if (drives.Count == 0)
        {
            int idx = 0;
            foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType is DriveType.Fixed or DriveType.Removable))
            {
                drives.Add(new StorageDriveDetails(
                    idx++,
                    $"{d.Name.TrimEnd('\\')} ({d.VolumeLabel})",
                    d.DriveType == DriveType.Removable ? "USB" : "Fixed",
                    d.DriveType == DriveType.Removable ? "USB / Removable" : "Fixed Volume",
                    Math.Round(d.TotalSize / (1000.0 * 1000.0 * 1000.0), 1),
                    d.DriveType == DriveType.Removable,
                    "N/A",
                    "OK (Logical Volume)",
                    null, null, null, null, null));
            }
        }

        return drives;
    }

    internal static bool IsExternalDrive(int busType, string wmiIface, string wmiMedia) =>
        busType == 7
        || wmiIface.Equals("USB", StringComparison.OrdinalIgnoreCase)
        || wmiMedia.Contains("External", StringComparison.OrdinalIgnoreCase);

    internal static string MapBusType(int busType, string model, string wmiIface) => busType switch
    {
        17 => "NVMe",
        11 => "SATA",
        7 => "USB",
        8 => "RAID",
        3 => "ATA",
        _ => model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe" : wmiIface
    };

    internal static string MapMediaCategory(int msftMedia, int busType, string model) => msftMedia switch
    {
        4 => "SSD",
        3 => "HDD",
        5 => "SCM",
        _ => busType == 17 || model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || model.Contains("SSD", StringComparison.OrdinalIgnoreCase)
            ? "SSD"
            : "HDD / Fixed"
    };

    internal static string MapHealthStatus(int healthCode) => healthCode switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "OK (WMI)"
    };

    /// <summary>Measures sequential write and read throughput on the target drive root (default %TEMP%).</summary>
    public async Task<(double ReadMbPerSec, double WriteMbPerSec)> RunSpeedTestAsync(
        string? driveRoot = null,
        int sizeMb = 128,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            string baseDir = string.IsNullOrWhiteSpace(driveRoot) ? Path.GetTempPath() : driveRoot;
            string testFile = Path.Combine(baseDir, $"laptoptester_bench_{Guid.NewGuid():N}.tmp");
            const int chunkBytes = 1024 * 1024;
            byte[] buffer = new byte[chunkBytes];
            Random.Shared.NextBytes(buffer);

            try
            {
                var swWrite = Stopwatch.StartNew();
                using (var fs = new FileStream(testFile, FileMode.Create, FileAccess.Write, FileShare.None, chunkBytes, FileOptions.WriteThrough))
                {
                    for (int i = 0; i < sizeMb; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        fs.Write(buffer, 0, buffer.Length);
                    }
                    fs.Flush(true);
                }
                swWrite.Stop();

                var swRead = Stopwatch.StartNew();
                using (var fs = new FileStream(testFile, FileMode.Open, FileAccess.Read, FileShare.None, chunkBytes, FileOptions.SequentialScan))
                {
                    while (fs.Read(buffer, 0, buffer.Length) > 0)
                        ct.ThrowIfCancellationRequested();
                }
                swRead.Stop();

                double writeSpeed = Math.Round(sizeMb / Math.Max(0.05, swWrite.Elapsed.TotalSeconds), 1);
                double readSpeed = Math.Round(sizeMb / Math.Max(0.05, swRead.Elapsed.TotalSeconds), 1);
                return (readSpeed, writeSpeed);
            }
            finally
            {
                try { if (File.Exists(testFile)) File.Delete(testFile); } catch { }
            }
        }, ct);
    }
}
