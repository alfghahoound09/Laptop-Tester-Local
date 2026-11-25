using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Services;

namespace LaptopTester.App.ViewModels;

public enum AppTab
{
    Dashboard,
    QuickTest,
    Hardware,
    Devices,
    Report,
    History,
    Settings
}

public partial class MainViewModel : ObservableObject
{
    private readonly LocalizationService _loc;
    private readonly TestResultsStore _store;

    [ObservableProperty] private AppTab _activeTab = AppTab.Dashboard;
    [ObservableProperty] private object? _currentViewModel;
    [ObservableProperty] private string _navDashboard = "";
    [ObservableProperty] private string _navQuickTest = "";
    [ObservableProperty] private string _navHardware = "";
    [ObservableProperty] private string _navDevices = "";
    [ObservableProperty] private string _navReport = "";
    [ObservableProperty] private string _navHistory = "";
    [ObservableProperty] private string _navSettings = "";
    [ObservableProperty] private string _appVersionLabel = "v1.0.0";
    [ObservableProperty] private string _privilegeShortLabel = "";
    [ObservableProperty] private string _sessionChecksBadge = "0";
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _isNavExpanded = true;
    [ObservableProperty] private double _navPaneWidth = 224;
    [ObservableProperty] private bool _hasAvailableUpdate;
    [ObservableProperty] private string _availableUpdateLabel = "";

    public DashboardViewModel Dashboard { get; }
    public QuickTestViewModel QuickTest { get; }
    public HardwareHubViewModel Hardware { get; }
    public DeviceHubViewModel Devices { get; }
    public ReportViewModel Report { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(
        LocalizationService loc,
        TestResultsStore store,
        DashboardViewModel dashboard,
        QuickTestViewModel quickTest,
        HardwareHubViewModel hardware,
        DeviceHubViewModel devices,
        ReportViewModel report,
        HistoryViewModel history,
        SettingsViewModel settings)
    {
        _loc = loc;
        _store = store;
        Dashboard = dashboard;
        QuickTest = quickTest;
        Hardware = hardware;
        Devices = devices;
        Report = report;
        History = history;
        Settings = settings;

        Dashboard.NavigateRequested += OnNavigateRequested;
        QuickTest.NavigateToDeviceRequested += tab =>
        {
            Devices.SelectSection(tab);
            Navigate(AppTab.Devices);
        };

        _store.ResultsChanged += (_, _) => RefreshSessionBadge();
        Settings.UpdateStatusChanged += res =>
        {
            HasAvailableUpdate = res.IsUpdateAvailable && res.PrimaryAsset is not null;
            AvailableUpdateLabel = HasAvailableUpdate ? $"Update {res.LatestRelease?.TagName}" : "";
        };

        var ver = typeof(MainViewModel).Assembly.GetName().Version;
        AppVersionLabel = ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0.0";
        PrivilegeShortLabel = HardwareService.IsCurrentProcessAdministrator() ? "ADMIN" : "STANDARD";
        RefreshLabels();
        RefreshSessionBadge();
        Navigate(AppTab.Dashboard);
    }

    private void OnNavigateRequested((AppTab Tab, string? SubSection) req)
    {
        if (req.Tab == AppTab.Hardware && !string.IsNullOrEmpty(req.SubSection))
            Hardware.SelectSubTab(req.SubSection);
        else if (req.Tab == AppTab.Devices && !string.IsNullOrEmpty(req.SubSection))
            Devices.SelectSection(req.SubSection);

        Navigate(req.Tab);
    }

    [RelayCommand]
    public void Navigate(AppTab tab)
    {
        ActiveTab = tab;
        CurrentViewModel = tab switch
        {
            AppTab.Dashboard => Dashboard,
            AppTab.QuickTest => QuickTest,
            AppTab.Hardware => Hardware,
            AppTab.Devices => Devices,
            AppTab.Report => Report,
            AppTab.History => History,
            AppTab.Settings => Settings,
            _ => Dashboard
        };
        CanGoBack = tab != AppTab.Dashboard;

        if (tab == AppTab.Report) Report.Refresh();
        if (tab == AppTab.History) _ = History.LoadAsync();
    }

    [RelayCommand]
    private void GoBack()
    {
        if (ActiveTab == AppTab.Devices && Devices.ActiveSection != "Hub")
        {
            Devices.SelectSection("Hub");
            return;
        }
        Navigate(AppTab.Dashboard);
    }

    [RelayCommand]
    private void ToggleNavPane()
    {
        IsNavExpanded = !IsNavExpanded;
        NavPaneWidth = IsNavExpanded ? 224 : 64;
    }

    [RelayCommand]
    private void OpenUpdateSettings() => Navigate(AppTab.Settings);

    public async Task CheckForUpdateInBackgroundAsync()
    {
        if (!Settings.AutoCheckUpdates) return;
        await Task.Delay(2200);
        await Settings.CheckQuietlyOnStartupAsync();
    }

    private void RefreshSessionBadge()
    {
        int count = _store.Results.Count;
        SessionChecksBadge = $"{count} check{(count == 1 ? "" : "s")}";
    }

    private void RefreshLabels()
    {
        NavDashboard = _loc.Get("Nav.Dashboard");
        NavQuickTest = _loc.Get("Nav.QuickTest");
        NavHardware = _loc.Get("Nav.Hardware");
        NavDevices = _loc.Get("Nav.Devices");
        NavReport = _loc.Get("Nav.Report");
        NavHistory = _loc.Get("Nav.History");
        NavSettings = _loc.Get("Nav.Settings");
        RefreshSessionBadge();
    }
}
