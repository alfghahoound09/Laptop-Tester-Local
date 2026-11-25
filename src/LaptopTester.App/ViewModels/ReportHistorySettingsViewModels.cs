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
    [ObservableProperty] private VerdictBand _verdictBand = VerdictBand.Good;
    [ObservableProperty] private string _verdictText = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _exportStatus = "";
    [ObservableProperty] private string _titleText = "";
    [ObservableProperty] private string _saveBtnText = "";
    [ObservableProperty] private string _exportJsonBtnText = "";
    [ObservableProperty] private string _exportHtmlBtnText = "";

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
        RefreshLabels();
        Refresh();
    }

    private void RefreshLabels()
    {
        TitleText = _loc.Get("Report.Title");
        SaveBtnText = _loc.Get("Report.SaveSession");
        ExportJsonBtnText = _loc.Get("Report.ExportJson");
        ExportHtmlBtnText = _loc.Get("Report.ExportHtml");
        if (string.IsNullOrEmpty(ExportStatus))
            ExportStatus = "Review your overall score, add inspection notes, and export HTML/PDF or JSON reports";
    }

    [RelayCommand]
    public void Refresh()
    {
        var (score, verdict, deductions) = _engine.Evaluate(_store.Results);
        Score = score;
        VerdictBand = verdict;
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
    private readonly LocalizationService _loc;

    [ObservableProperty] private QuickTestSession? _selectedSession;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _titleText = "";
    [ObservableProperty] private string _refreshBtnText = "";
    [ObservableProperty] private string _clearBtnText = "";
    public ObservableCollection<QuickTestSession> Sessions { get; } = new();

    public HistoryViewModel(HistoryReportService historyService, LocalizationService loc)
    {
        _historyService = historyService;
        _loc = loc;
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        TitleText = _loc.Get("History.Title");
        RefreshBtnText = _loc.Get("History.Refresh");
        ClearBtnText = _loc.Get("History.Clear");
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

    [ObservableProperty] private string _privilegeText = "";
    [ObservableProperty] private string _historyFolder = "";
    [ObservableProperty] private string _titleText = "";
    [ObservableProperty] private string _privilegeHeader = "";

    public SettingsViewModel(LocalizationService loc, HistoryReportService historyService)
    {
        _loc = loc;
        _historyService = historyService;
        PrivilegeText = HardwareService.IsCurrentProcessAdministrator()
            ? "Administrator — Full hardware, drive health & motherboard sensor access enabled"
            : "Standard User — Core checks work normally (run as Administrator to read CPU motherboard temperature sensors)";
        HistoryFolder = _historyService.HistoryDirectory;
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        TitleText = _loc.Get("Settings.Title");
        PrivilegeHeader = _loc.Get("Settings.Privilege");
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
