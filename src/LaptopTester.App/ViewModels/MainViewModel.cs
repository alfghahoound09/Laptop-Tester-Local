using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaptopTester.Core.Localization;

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

    [ObservableProperty] private AppTab _activeTab = AppTab.Dashboard;
    [ObservableProperty] private object? _currentViewModel;
    [ObservableProperty] private string _navDashboard = "";
    [ObservableProperty] private string _navQuickTest = "";
    [ObservableProperty] private string _navHardware = "";
    [ObservableProperty] private string _navDevices = "";
    [ObservableProperty] private string _navReport = "";
    [ObservableProperty] private string _navHistory = "";
    [ObservableProperty] private string _languageLabel = "VI";
    [ObservableProperty] private bool _canGoBack;

    public DashboardViewModel Dashboard { get; }
    public QuickTestViewModel QuickTest { get; }
    public HardwareHubViewModel Hardware { get; }
    public DeviceHubViewModel Devices { get; }
    public ReportViewModel Report { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(
        LocalizationService loc,
        DashboardViewModel dashboard,
        QuickTestViewModel quickTest,
        HardwareHubViewModel hardware,
        DeviceHubViewModel devices,
        ReportViewModel report,
        HistoryViewModel history,
        SettingsViewModel settings)
    {
        _loc = loc;
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

        _loc.LanguageChanged += (_, _) => RefreshLabels();
        RefreshLabels();
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
    private void ToggleLanguage()
    {
        _loc.SetLanguage(_loc.CurrentLanguage == AppLanguage.Vietnamese ? AppLanguage.English : AppLanguage.Vietnamese);
    }

    private void RefreshLabels()
    {
        NavDashboard = _loc.Get("Nav.Dashboard");
        NavQuickTest = _loc.Get("Nav.QuickTest");
        NavHardware = _loc.Get("Nav.Hardware");
        NavDevices = _loc.Get("Nav.Devices");
        NavReport = _loc.Get("Nav.Report");
        NavHistory = _loc.Get("Nav.History");
        LanguageLabel = _loc.CurrentLanguage == AppLanguage.Vietnamese ? "VI" : "EN";
    }
}
