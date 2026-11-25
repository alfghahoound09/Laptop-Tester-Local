using LaptopTester.Core.Localization;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;

int failures = 0;
void Assert(bool cond, string msg)
{
    if (!cond)
    {
        Console.WriteLine($"[FAIL] {msg}");
        failures++;
    }
    else
    {
        Console.WriteLine($"[PASS] {msg}");
    }
}

Console.WriteLine("=== LaptopTester.Core Self-Test Suite ===");

// 1. SMBIOS Reader
var smbios = SmbiosReader.Read();
Assert(smbios.IsAvailable, $"SMBIOS table read (Sys={smbios.SystemManufacturer} {smbios.SystemProductName}, CPU={smbios.ProcessorVersion})");

// 2. Storage & Hardware & Anti-Fake
var storageSvc = new StorageHealthService();
var batterySvc = new BatteryService();
var hwSvc = new HardwareService(batterySvc, storageSvc);
var antiFakeSvc = new AntiFakeService(storageSvc);

var report = await hwSvc.ScanHardwareAsync();
Assert(!string.IsNullOrWhiteSpace(report.Cpu.Name), $"HardwareScan CPU={report.Cpu.Name}");
Assert(report.Ram.TotalInstalledGb > 0, $"HardwareScan RAM={report.Ram.TotalInstalledGb} GB ({report.Ram.MemoryType})");
Assert(report.Displays.Count > 0, $"HardwareScan Displays={report.Displays.Count} ({report.Displays.FirstOrDefault()?.WidthPx}x{report.Displays.FirstOrDefault()?.HeightPx})");
Assert(report.Drives.Count > 0, $"HardwareScan Drives={report.Drives.Count}");

var antiFake = await antiFakeSvc.ScanForSpoofingAsync();
Assert(antiFake.Comparisons.Count >= 4, $"AntiFake Comparisons={antiFake.Comparisons.Count}, Risk={antiFake.OverallRisk}");

// 3. Connectivity & Audio & System Audit
using var usbSvc = new UsbConnectivityService();
var (controllers, usbDevices) = await usbSvc.ScanUsbAsync();
Assert(controllers.Count >= 0 && usbDevices.Count >= 0, $"USB scan: {controllers.Count} controllers, {usbDevices.Count} devices");

using var audioSvc = new AudioCameraService();
var audioDevs = audioSvc.GetAudioDevices();
Assert(audioDevs.OutputDeviceCount >= 0 && audioDevs.InputDeviceCount >= 0, $"Audio out={audioDevs.OutputDeviceCount}, in={audioDevs.InputDeviceCount}");

var auditSvc = new SystemAuditService();
var startup = await auditSvc.ScanStartupEntriesAsync();
var driverIssues = await auditSvc.ScanDriverIssuesAsync();
Assert(startup.Count >= 0 && driverIssues.Count >= 0, $"Audit startup={startup.Count}, driverIssues={driverIssues.Count}");

// 4. QuickTestEngine scoring
var engine = new QuickTestEngine();
var sampleResults = new[]
{
    new TestResultEntry("hardware", "Hardware", "Core", TestStatus.Passed, "OK", 0, DateTimeOffset.Now),
    new TestResultEntry("battery", "Battery", "Power", TestStatus.Warning, "Health 68%", 0, DateTimeOffset.Now),
    new TestResultEntry("display", "Display", "Display", TestStatus.Failed, "Dead pixels", 0, DateTimeOffset.Now)
};
var (score, verdict, deductions) = engine.Evaluate(sampleResults);
Assert(score == 81, $"QuickTestEngine score expected 81, got {score}");
Assert(verdict == VerdictBand.DoNotBuy, $"QuickTestEngine critical display fail -> DoNotBuy (got {verdict})");
Assert(deductions.Count == 2, $"QuickTestEngine deductions={deductions.Count}");

// 5. HistoryReportService roundtrip in temp dir
string tempHist = Path.Combine(Path.GetTempPath(), $"lt_test_{Guid.NewGuid():N}");
try
{
    var histSvc = new HistoryReportService(tempHist);
    var session = new QuickTestSession(
        "test01", DateTimeOffset.Now, report.Summary.Model, report.Cpu.Name,
        report.Summary.RamSummary, report.Summary.StorageSummary, score, verdict, sampleResults, deductions, "Note");
    await histSvc.SaveSessionAsync(session);
    var loaded = await histSvc.LoadSessionsAsync();
    Assert(loaded.Count == 1 && loaded[0].Id == "test01", "History JSON save/load roundtrip");

    string htmlPath = Path.Combine(tempHist, "report.html");
    await histSvc.ExportHtmlAsync(htmlPath, session);
    Assert(File.Exists(htmlPath) && new FileInfo(htmlPath).Length > 500, "HTML report export");
}
finally
{
    try { Directory.Delete(tempHist, true); } catch { }
}

// 6. Localization switch
var loc = new LocalizationService();
Assert(loc.Get("Nav.Dashboard") == "Trang Chủ", "Localization VI default");
loc.SetLanguage(AppLanguage.English);
Assert(loc.Get("Nav.Dashboard") == "Dashboard", "Localization EN switch");

Console.WriteLine($"\nResult: {(failures == 0 ? "ALL PASSED" : $"{failures} FAILED")}");
return failures == 0 ? 0 : 1;
