using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LaptopTester.App.ViewModels;
using LaptopTester.App.Views;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LaptopTester.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        bool verifyMode = e.Args.Length >= 2 && e.Args[0] == "--verify-render";
        if (verifyMode)
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        if (verifyMode)
        {
            try
            {
                await RunVisualVerificationAsync(mainWindow, _serviceProvider.GetRequiredService<MainViewModel>(), e.Args[1]);
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(e.Args[1]);
                File.WriteAllText(Path.Combine(e.Args[1], "verify_error.txt"), ex.ToString());
            }
            Shutdown(0);
        }
    }

    private static async Task RunVisualVerificationAsync(MainWindow win, MainViewModel vm, string outDir)
    {
        Directory.CreateDirectory(outDir);
        await Task.Delay(2500); // allow initial hardware scan to populate

        await SaveWindowRenderAsync(win, Path.Combine(outDir, "01_dashboard.png"));

        vm.Navigate(AppTab.QuickTest);
        await Task.Delay(350);
        await SaveWindowRenderAsync(win, Path.Combine(outDir, "02_quicktest.png"));

        vm.Navigate(AppTab.Hardware);
        await vm.Hardware.RunAntiFakeScanAsync();
        vm.Hardware.SelectSubTab("AntiFake");
        await Task.Delay(350);
        await SaveWindowRenderAsync(win, Path.Combine(outDir, "03_antifake.png"));

        vm.Hardware.SelectSubTab("Battery");
        await vm.Hardware.CheckBatteryAsync();
        await Task.Delay(350);
        await SaveWindowRenderAsync(win, Path.Combine(outDir, "04_battery.png"));

        vm.Navigate(AppTab.Devices);
        vm.Devices.SelectSection("Keyboard");
        await Task.Delay(350);
        await SaveWindowRenderAsync(win, Path.Combine(outDir, "05_keyboard.png"));
    }

    private static async Task SaveWindowRenderAsync(Window win, string path)
    {
        await win.Dispatcher.InvokeAsync(() =>
        {
            win.UpdateLayout();
            int w = Math.Max(1, (int)Math.Ceiling(win.ActualWidth));
            int h = Math.Max(1, (int)Math.Ceiling(win.ActualHeight));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            if (win.Content is Visual contentVisual)
                rtb.Render(contentVisual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }, DispatcherPriority.Render);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<TestResultsStore>();
        services.AddSingleton<BatteryService>();
        services.AddSingleton<StorageHealthService>();
        services.AddSingleton<HardwareService>();
        services.AddSingleton<AntiFakeService>();
        services.AddSingleton<UsbConnectivityService>();
        services.AddSingleton<AudioCameraService>();
        services.AddSingleton<BenchmarkThermalService>();
        services.AddSingleton<SystemAuditService>();
        services.AddSingleton<QuickTestEngine>();
        services.AddSingleton<HistoryReportService>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<HardwareHubViewModel>();
        services.AddSingleton<DeviceHubViewModel>();
        services.AddSingleton<QuickTestViewModel>();
        services.AddSingleton<ReportViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Unexpected UI error: {e.Exception.Message}", "LaptopTester", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
