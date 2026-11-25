using System.Collections.Concurrent;

namespace LaptopTester.Core.Localization;

/// <summary>Provides clear, centralized English UI copy and diagnostic verdict text.</summary>
public sealed class LocalizationService
{
    private static readonly ConcurrentDictionary<string, string> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["App.Subtitle"] = "Laptop hardware diagnostics & pre-purchase inspection",
        ["Nav.Dashboard"] = "Dashboard",
        ["Nav.QuickTest"] = "Quick Test",
        ["Nav.Hardware"] = "Hardware",
        ["Nav.Devices"] = "Devices",
        ["Nav.Report"] = "Report",
        ["Nav.History"] = "History",
        ["Nav.Settings"] = "Settings",

        ["Dashboard.Title"] = "System Overview",
        ["Dashboard.Subtitle"] = "Live hardware summary, component specs, and laptop health",
        ["Dashboard.Tile.Hardware"] = "Hardware Specs",
        ["Dashboard.Tile.Battery"] = "Battery Health",
        ["Dashboard.Tile.AntiFake"] = "Anti-Fake Check",
        ["Dashboard.Tile.Benchmark"] = "Benchmark",
        ["Dashboard.SystemInfo"] = "System Information",
        ["Dashboard.Rescan"] = "Refresh Specs",
        ["Dashboard.AboutTitle"] = "LaptopTester Open Edition",
        ["Dashboard.AboutDesc"] = "All-in-one laptop inspection tool. Verifies genuine hardware specs across Windows and BIOS, measures real battery wear, checks screen pixels, keyboard, audio, USB ports, and exports shareable reports.",
        ["Dashboard.Tip"] = "Tip: Press F2 (Quick Test) to walk through 10 essential checks and get an overall health score out of 100 before buying or selling a laptop.",

        ["AntiFake.Title"] = "Genuine Hardware Audit (Anti-Fake)",
        ["AntiFake.Subtitle"] = "Compares Windows system info, Registry, and low-level BIOS firmware (SMBIOS) to catch altered specs or relabeled drives",
        ["AntiFake.ScanBtn"] = "Run Genuine Spec Check",
        ["AntiFake.Clean"] = "Verified — All hardware specs match across Windows and firmware",
        ["AntiFake.Warning"] = "Warning — Mismatched or suspicious hardware details found",
        ["AntiFake.SourceHeader"] = "3-SOURCE HARDWARE VERIFICATION",
        ["AntiFake.SourceSub"] = "Genuine parts report the same identity in Windows (WMI), Registry, and BIOS firmware (SMBIOS). Differences suggest modified or spoofed specs.",

        ["Battery.Title"] = "Battery Health & Drain Test",
        ["Battery.Subtitle"] = "Checks factory design capacity vs. current max charge, battery wear, charge cycles, and real drain under load",
        ["Battery.ScanBtn"] = "Check Battery",
        ["Battery.StressStart"] = "Start 30s Drain Test",
        ["Battery.StressStop"] = "Stop Drain Test",

        ["QuickTest.Title"] = "Quick Test Wizard (10 Steps)",
        ["QuickTest.Subtitle"] = "Step-by-step inspection of core hardware, storage, battery, screen, and peripherals for a /100 health score",
        ["QuickTest.RunAuto"] = "Run Automatic Checks",
        ["QuickTest.Pass"] = "Pass",
        ["QuickTest.Fail"] = "Fail",
        ["QuickTest.Skip"] = "Skip",

        ["Verdict.Good"] = "Good — Everything looks healthy",
        ["Verdict.Consider"] = "Use Caution — Review flagged items",
        ["Verdict.DoNotBuy"] = "Not Recommended — Serious issues found",

        ["Report.Title"] = "Diagnostic Report",
        ["Report.ExportHtml"] = "Export HTML / Print PDF",
        ["Report.ExportJson"] = "Export JSON",
        ["Report.SaveSession"] = "Save to History",

        ["History.Title"] = "Test History",
        ["History.Refresh"] = "Refresh",
        ["History.Clear"] = "Clear All",

        ["Settings.Title"] = "Settings",
        ["Settings.Privilege"] = "App Permissions (Administrator Mode)"
    };

    public string Get(string key) => Table.TryGetValue(key, out string? value) ? value : key;

    internal static IReadOnlyCollection<string> AllKeys => Table.Keys.ToArray();
}
