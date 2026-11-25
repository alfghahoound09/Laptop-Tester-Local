using System.IO;
using LaptopTester.Core.Models;
using Microsoft.Win32;

namespace LaptopTester.Core.Services;

/// <summary>Audits Windows device driver error codes and startup entries.</summary>
public sealed class SystemAuditService
{
    public async Task<IReadOnlyList<DriverIssueDetails>> ScanDriverIssuesAsync(CancellationToken ct = default)
    {
        var rows = await WmiQueryHelper.QueryAsync(
            "SELECT Name, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0",
            ct: ct);

        return rows.Select(r =>
        {
            int code = WmiQueryHelper.Int(r, "ConfigManagerErrorCode");
            return new DriverIssueDetails(
                WmiQueryHelper.Str(r, "Name", "Unknown Device"),
                WmiQueryHelper.Str(r, "PNPClass", "System"),
                code,
                DescribeCmError(code));
        }).ToList();
    }

    public Task<IReadOnlyList<StartupEntryDetails>> ScanStartupEntriesAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<StartupEntryDetails>>(() =>
        {
            var entries = new List<StartupEntryDetails>();

            ReadRunKey(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKCU\\Run", entries);
            ReadRunKey(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM\\Run", entries);
            ReadRunKey(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU\\RunOnce", entries);
            ReadRunKey(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM\\RunOnce", entries);

            foreach (var folder in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
            })
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(folder)) continue;
                try
                {
                    foreach (var file in Directory.GetFiles(folder))
                    {
                        string name = Path.GetFileName(file);
                        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        entries.Add(new StartupEntryDetails(name, file, "Startup Folder", "Normal"));
                    }
                }
                catch { }
            }

            return entries;
        }, ct);
    }

    private static void ReadRunKey(RegistryHive hive, string path, string location, List<StartupEntryDetails> entries)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                string cmd = key.GetValue(name)?.ToString() ?? "";
                string risk = cmd.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
                    || cmd.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                    || cmd.Contains("wscript", StringComparison.OrdinalIgnoreCase)
                    ? "Review"
                    : "Normal";
                entries.Add(new StartupEntryDetails(name, cmd, location, risk));
            }
        }
        catch { }
    }

    private static string DescribeCmError(int code) => code switch
    {
        1 => "Device is not configured correctly (Code 1)",
        10 => "Device cannot start (Code 10)",
        12 => "Not enough free resources (Code 12)",
        18 => "Reinstall the drivers for this device (Code 18)",
        22 => "Device is disabled (Code 22)",
        28 => "Drivers are not installed (Code 28)",
        31 => "Device is not working properly (Code 31)",
        43 => "Windows stopped this device because it reported problems (Code 43)",
        45 => "Hardware device is not currently connected (Code 45)",
        _ => $"Device Manager error Code {code}"
    };
}
