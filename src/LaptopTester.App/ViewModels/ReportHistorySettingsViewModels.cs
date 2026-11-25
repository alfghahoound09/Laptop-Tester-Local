using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
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
    private readonly GitHubUpdateService _updateService;
    private readonly Action _shutdownApp;
    private readonly bool _isInitializing = true;
    private UpdateCheckResult? _latestCheck;

    public event Action<UpdateCheckResult>? UpdateStatusChanged;

    [ObservableProperty] private string _privilegeText = "";
    [ObservableProperty] private string _historyFolder = "";
    [ObservableProperty] private string _titleText = "";
    [ObservableProperty] private string _privilegeHeader = "";

    // In-App GitHub Updater State
    [ObservableProperty] private string _currentVersionText = "";
    [ObservableProperty] private string _installModeBadge = "";
    [ObservableProperty] private string _lastCheckedText = "Never";
    [ObservableProperty] private bool _autoCheckUpdates = true;
    [ObservableProperty] private string _gitHubTokenInput = "";
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private bool _hasAvailableUpdate;
    [ObservableProperty] private string _availableUpdateTag = "";
    [ObservableProperty] private string _updateStatusText = "Click 'Check for Updates' to check GitHub for a newer release.";
    [ObservableProperty] private int _updateDownloadPercent;
    [ObservableProperty] private string _updateReleaseNotes = "";
    [ObservableProperty] private string _updateReleaseUrl = GitHubUpdateService.DefaultReleasesUrl;

    public bool IsUpdateIdle => !IsCheckingUpdates && !IsDownloadingUpdate;
    public bool CanInstallUpdate => HasAvailableUpdate && IsUpdateIdle && _latestCheck?.PrimaryAsset is not null;

    public SettingsViewModel(
        LocalizationService loc,
        HistoryReportService historyService,
        GitHubUpdateService? updateService = null,
        Action? shutdownApp = null)
    {
        _loc = loc;
        _historyService = historyService;
        _updateService = updateService ?? new GitHubUpdateService();
        _shutdownApp = shutdownApp ?? (() => Application.Current?.Dispatcher.Invoke(() => Application.Current.Shutdown()));

        PrivilegeText = HardwareService.IsCurrentProcessAdministrator()
            ? "Administrator — Full hardware, drive health & motherboard sensor access enabled"
            : "Standard User — Core checks work normally (run as Administrator to read CPU motherboard temperature sensors)";
        HistoryFolder = _historyService.HistoryDirectory;

        CurrentVersionText = $"v{GitHubUpdateService.GetCurrentVersion()}";
        InstallModeBadge = FormatInstallMode(GitHubUpdateService.DetectInstallMode(Environment.ProcessPath));
        UpdateReleaseUrl = _updateService.ReleasesPageUrl;

        var saved = _updateService.LoadSettings();
        _autoCheckUpdates = saved.AutoCheckOnStartup;
        _gitHubTokenInput = saved.GitHubToken ?? "";
        LastCheckedText = saved.LastCheckedAt.HasValue
            ? saved.LastCheckedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "Never";

        _isInitializing = false;
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        TitleText = _loc.Get("Settings.Title");
        PrivilegeHeader = _loc.Get("Settings.Privilege");
    }

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        if (_isInitializing) return;
        var s = _updateService.LoadSettings();
        s.AutoCheckOnStartup = value;
        _updateService.SaveSettings(s);
    }

    partial void OnIsCheckingUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(IsUpdateIdle));
        OnPropertyChanged(nameof(CanInstallUpdate));
    }

    partial void OnIsDownloadingUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(IsUpdateIdle));
        OnPropertyChanged(nameof(CanInstallUpdate));
    }

    partial void OnHasAvailableUpdateChanged(bool value) => OnPropertyChanged(nameof(CanInstallUpdate));

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdates || IsDownloadingUpdate) return;
        IsCheckingUpdates = true;
        UpdateStatusText = "Checking GitHub Releases for updates...";
        try
        {
            var res = await _updateService.CheckForUpdatesAsync(
                tokenOverride: string.IsNullOrWhiteSpace(GitHubTokenInput) ? null : GitHubTokenInput.Trim());
            ApplyCheckResult(res);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    public async Task CheckQuietlyOnStartupAsync()
    {
        if (!AutoCheckUpdates || IsCheckingUpdates || IsDownloadingUpdate) return;
        IsCheckingUpdates = true;
        try
        {
            var res = await _updateService.CheckForUpdatesAsync(
                tokenOverride: string.IsNullOrWhiteSpace(GitHubTokenInput) ? null : GitHubTokenInput.Trim());
            if (res.LatestRelease is not null)
                ApplyCheckResult(res);
        }
        catch { }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    public async Task InstallUpdateAsync()
    {
        if (_latestCheck is null || !_latestCheck.IsUpdateAvailable || _latestCheck.PrimaryAsset is null)
            return;
        if (IsDownloadingUpdate || IsCheckingUpdates)
            return;

        IsDownloadingUpdate = true;
        UpdateDownloadPercent = 0;
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                UpdateDownloadPercent = p.Percent;
                UpdateStatusText = p.StatusText;
            });

            var prepared = await _updateService.DownloadAndVerifyAsync(
                _latestCheck,
                progress,
                tokenOverride: string.IsNullOrWhiteSpace(GitHubTokenInput) ? null : GitHubTokenInput.Trim());

            UpdateStatusText = prepared.InstallMode == AppInstallMode.Portable
                ? $"Replacing {Path.GetFileName(prepared.TargetExePath)} cleanly and restarting..."
                : $"Installing {prepared.VersionTag} silently and restarting...";

            _updateService.LaunchUpdaterProcess(prepared);
            _shutdownApp();
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    [RelayCommand]
    private void SaveToken()
    {
        var s = _updateService.LoadSettings();
        s.GitHubToken = string.IsNullOrWhiteSpace(GitHubTokenInput) ? null : GitHubTokenInput.Trim();
        _updateService.SaveSettings(s);
        UpdateStatusText = string.IsNullOrWhiteSpace(s.GitHubToken)
            ? "Cleared optional GitHub token."
            : "Saved GitHub token locally for private repository access.";
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (!GitHubUpdateService.IsTrustedGitHubUrl(UpdateReleaseUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(UpdateReleaseUrl) { UseShellExecute = true });
        }
        catch { }
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

    private void ApplyCheckResult(UpdateCheckResult res)
    {
        _latestCheck = res;
        HasAvailableUpdate = res.IsUpdateAvailable && res.PrimaryAsset is not null;
        AvailableUpdateTag = res.LatestRelease?.TagName ?? "";
        UpdateStatusText = res.StatusMessage;
        UpdateReleaseNotes = res.LatestRelease?.ReleaseNotes ?? "";
        UpdateReleaseUrl = res.LatestRelease?.HtmlUrl ?? _updateService.ReleasesPageUrl;
        InstallModeBadge = FormatInstallMode(res.InstallMode);

        var s = _updateService.LoadSettings();
        LastCheckedText = s.LastCheckedAt.HasValue
            ? s.LastCheckedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : DateTime.Now.ToString("yyyy-MM-dd HH:mm");

        OnPropertyChanged(nameof(CanInstallUpdate));
        UpdateStatusChanged?.Invoke(res);
    }

    private static string FormatInstallMode(AppInstallMode mode) => mode switch
    {
        AppInstallMode.Installer => "Installed App (Silent In-Place)",
        AppInstallMode.Portable => "Portable EXE (Hot-Swap)",
        _ => "Development Build"
    };
}
