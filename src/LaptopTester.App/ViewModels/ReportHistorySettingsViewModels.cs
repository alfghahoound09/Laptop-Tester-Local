using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;
using Microsoft.Win32;

namespace LaptopTester.App.ViewModels;

public partial class ReportViewModel : ObservableObject
{
    private readonly TestResultsStore _store;
    private readonly QuickTestEngine _engine;
    private readonly HardwareService _hardwareService;
    private readonly HistoryReportService _historyService;
    private readonly LocalizationService _loc;

    [ObservableProperty] private int _score = 100;
    [ObservableProperty] private string _verdictText = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _exportStatus = "";

    public ObservableCollection<TestResultEntry> Results => _store.Results;
    public ObservableCollection<ScoreDeduction> Deductions { get; } = new();

    public ReportViewModel(
        TestResultsStore store,
        QuickTestEngine engine,
        HardwareService hardwareService,
        HistoryReportService historyService,
        LocalizationService loc)
    {
        _store = store;
        _engine = engine;
        _hardwareService = hardwareService;
        _historyService = historyService;
        _loc = loc;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        var (score, verdict, deductions) = _engine.Evaluate(_store.Results);
        Score = score;
        VerdictText = verdict switch
        {
            VerdictBand.Good => _loc.Get("Verdict.Good"),
            VerdictBand.ConsiderCarefully => _loc.Get("Verdict.Consider"),
            _ => _loc.Get("Verdict.DoNotBuy")
        };
        Deductions.Clear();
        foreach (var d in deductions) Deductions.Add(d);
    }

    private async Task<QuickTestSession> BuildSessionAsync()
    {
        var hw = await _hardwareService.ScanHardwareAsync();
        var (score, verdict, deductions) = _engine.Evaluate(_store.Results);
        return new QuickTestSession(
            Guid.NewGuid().ToString("N")[..8],
            DateTimeOffset.Now,
            $"{hw.Summary.Manufacturer} {hw.Summary.Model}".Trim(),
            hw.Summary.CpuName,
            hw.Summary.RamSummary,
            hw.Summary.StorageSummary,
            score,
            verdict,
            _store.Results.ToList(),
            deductions,
            Notes);
    }

    [RelayCommand]
    private async Task SaveSessionAsync()
    {
        var session = await BuildSessionAsync();
        await _historyService.SaveSessionAsync(session);
        ExportStatus = $"Saved session {session.Id} to History";
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "HTML Report (*.html)|*.html",
            FileName = $"LaptopTester_Report_{DateTime.Now:yyyyMMdd_HHmm}.html"
        };
        if (dlg.ShowDialog() != true) return;
        var session = await BuildSessionAsync();
        await _historyService.ExportHtmlAsync(dlg.FileName, session);
        ExportStatus = $"Exported HTML: {dlg.FileName}";
        try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON Data (*.json)|*.json",
            FileName = $"LaptopTester_Report_{DateTime.Now:yyyyMMdd_HHmm}.json"
        };
        if (dlg.ShowDialog() != true) return;
        var session = await BuildSessionAsync();
        await _historyService.ExportJsonAsync(dlg.FileName, session);
        ExportStatus = $"Exported JSON: {dlg.FileName}";
    }
}

public partial class HistoryViewModel : ObservableObject
{
    private readonly HistoryReportService _historyService;

    [ObservableProperty] private QuickTestSession? _selectedSession;
    [ObservableProperty] private string _statusText = "";
    public ObservableCollection<QuickTestSession> Sessions { get; } = new();

    public HistoryViewModel(HistoryReportService historyService)
    {
        _historyService = historyService;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var list = await _historyService.LoadSessionsAsync();
        Sessions.Clear();
        foreach (var s in list) Sessions.Add(s);
        StatusText = $"{Sessions.Count} saved session(s)";
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedSession is null) return;
        _historyService.DeleteSession(SelectedSession.Id);
        SelectedSession = null;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        _historyService.ClearAllHistory();
        SelectedSession = null;
        await LoadAsync();
    }
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly LocalizationService _loc;
    private readonly HistoryReportService _historyService;

    [ObservableProperty] private bool _isVietnamese;
    [ObservableProperty] private string _privilegeText = "";
    [ObservableProperty] private string _historyFolder = "";

    public SettingsViewModel(LocalizationService loc, HistoryReportService historyService)
    {
        _loc = loc;
        _historyService = historyService;
        _isVietnamese = loc.CurrentLanguage == AppLanguage.Vietnamese;
        PrivilegeText = HardwareService.IsCurrentProcessAdministrator()
            ? "Administrator (Full ACPI / SMART access)"
            : "Standard User (WMI / SMBIOS / Registry / USB / Audio active)";
        HistoryFolder = _historyService.HistoryDirectory;
    }

    partial void OnIsVietnameseChanged(bool value)
    {
        _loc.SetLanguage(value ? AppLanguage.Vietnamese : AppLanguage.English);
    }

    [RelayCommand]
    private void OpenHistoryFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(HistoryFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", HistoryFolder) { UseShellExecute = true });
        }
        catch { }
    }
}
