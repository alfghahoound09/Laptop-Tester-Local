using System.Collections.Concurrent;

namespace LaptopTester.Core.Localization;

public enum AppLanguage
{
    Vietnamese,
    English
}

/// <summary>Bilingual localization provider (Vietnamese / English) with live change notifications.</summary>
public sealed class LocalizationService
{
    public AppLanguage CurrentLanguage { get; private set; } = AppLanguage.Vietnamese;
    public event EventHandler? LanguageChanged;

    private static readonly ConcurrentDictionary<string, (string Vi, string En)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["App.Subtitle"] = ("Bộ kiểm tra & chẩn đoán laptop mã nguồn mở", "Open-source laptop hardware & peripheral diagnostics"),
        ["Nav.Dashboard"] = ("Trang Chủ", "Dashboard"),
        ["Nav.QuickTest"] = ("Quy Trình", "Quick Test"),
        ["Nav.Hardware"] = ("Phần Cứng", "Hardware"),
        ["Nav.Devices"] = ("Thiết Bị", "Devices"),
        ["Nav.Report"] = ("Báo Cáo", "Report"),
        ["Nav.History"] = ("Lịch Sử", "History"),
        ["Nav.Settings"] = ("Cài Đặt", "Settings"),

        ["Dashboard.Title"] = ("Tổng Quan Hệ Thống", "System Overview"),
        ["Dashboard.Subtitle"] = ("Thông tin phần cứng và trạng thái máy tính theo thời gian thực", "Real-time hardware identity and laptop health status"),
        ["Dashboard.Tile.Hardware"] = ("Quét Phần Cứng", "Hardware Scan"),
        ["Dashboard.Tile.Battery"] = ("Kiểm Tra Pin", "Battery Health"),
        ["Dashboard.Tile.AntiFake"] = ("Chống Fake", "Anti-Fake"),
        ["Dashboard.Tile.Benchmark"] = ("Benchmark", "Benchmark"),
        ["Dashboard.SystemInfo"] = ("Thông Tin Hệ Thống", "System Information"),
        ["Dashboard.Rescan"] = ("Quét lại", "Rescan"),
        ["Dashboard.AboutTitle"] = ("LaptopTester Open Edition", "LaptopTester Open Edition"),
        ["Dashboard.AboutDesc"] = ("Công cụ kiểm tra laptop toàn diện với đối chiếu đa nguồn (WMI / Registry / SMBIOS), kiểm tra pin, màn hình, bàn phím, USB, âm thanh và xuất báo cáo HTML/JSON.", "Comprehensive laptop diagnostic suite with 3-source anti-fake cross-checks (WMI / Registry / SMBIOS), battery, display, keyboard, USB, audio, and HTML/JSON reporting."),
        ["Dashboard.Tip"] = ("Mẹo: Nhấn 'Quy Trình' (F2) để chạy 10 bước kiểm tra nhanh và chấm điểm /100 trước khi mua bán máy.", "Tip: Open 'Quick Test' (F2) for the guided 10-step diagnostic wizard with a /100 score and verdict."),

        ["AntiFake.Title"] = ("Chống Giả Mạo Phần Cứng", "Hardware Anti-Fake Audit"),
        ["AntiFake.Subtitle"] = ("Đối chiếu độc lập WMI, Registry và SMBIOS firmware để phát hiện máy sửa thông số", "Cross-check WMI, Registry, and raw SMBIOS firmware tables to detect faked specs"),
        ["AntiFake.ScanBtn"] = ("Quét Phát Hiện", "Run Anti-Fake Scan"),
        ["AntiFake.Clean"] = ("Thông số phần cứng nhất quán giữa các nguồn", "Hardware specs are consistent across all sources"),
        ["AntiFake.Warning"] = ("Phát hiện sai lệch hoặc dấu hiệu cần kiểm tra kỹ", "Detected mismatches or indicators worth checking"),
        ["AntiFake.SourceHeader"] = ("SO SÁNH THÔNG SỐ TỪ NHIỀU NGUỒN", "MULTI-SOURCE COMPARISON"),
        ["AntiFake.SourceSub"] = ("Nếu thông số khác nhau giữa các nguồn → Có thể đã bị chỉnh sửa hiển thị", "Discrepancies across sources may indicate display spoofing"),

        ["Battery.Title"] = ("Kiểm Tra Pin", "Battery Health & Stress Test"),
        ["Battery.Subtitle"] = ("Đọc dung lượng thiết kế, dung lượng thực, chu kỳ sạc và chạy stress test xả pin", "Inspect design vs full capacity, cycle count, and run real drain stress tests"),
        ["Battery.ScanBtn"] = ("Kiểm tra pin", "Check Battery"),
        ["Battery.StressStart"] = ("Bắt đầu Stress Test", "Start Stress Test"),
        ["Battery.StressStop"] = ("Dừng Stress Test", "Stop Stress Test"),

        ["QuickTest.Title"] = ("Quy Trình Kiểm Tra Nhanh (10 Bước)", "Quick Test Wizard (10 Steps)"),
        ["QuickTest.Subtitle"] = ("Kiểm tra tuần tự phần cứng, ổ cứng, pin, màn hình và ngoại vi để chấm điểm /100", "Guided diagnostic flow across hardware, storage, battery, display, and peripherals"),
        ["QuickTest.RunAuto"] = ("Chạy bước tự động", "Run Auto Checks"),
        ["QuickTest.Pass"] = ("Đạt", "Pass"),
        ["QuickTest.Fail"] = ("Lỗi", "Fail"),
        ["QuickTest.Skip"] = ("Bỏ qua", "Skip"),

        ["Verdict.Good"] = ("Máy tốt — An toàn để chọn mua", "Good — Safe to buy"),
        ["Verdict.Consider"] = ("Cần cân nhắc — Có điểm cần lưu ý", "Consider carefully — Review deductions"),
        ["Verdict.DoNotBuy"] = ("Không nên mua — Phát hiện lỗi nghiêm trọng", "Do not buy — Critical issues detected"),

        ["Report.Title"] = ("Báo Cáo Tổng Hợp", "Diagnostic Report"),
        ["Report.ExportHtml"] = ("Xuất HTML / In PDF", "Export HTML / Print PDF"),
        ["Report.ExportJson"] = ("Xuất JSON", "Export JSON"),
        ["Report.SaveSession"] = ("Lưu vào Lịch Sử", "Save to History"),

        ["History.Title"] = ("Lịch Sử Kiểm Tra", "Test History"),
        ["History.Refresh"] = ("Làm mới", "Refresh"),
        ["History.Clear"] = ("Xóa tất cả", "Clear All"),

        ["Settings.Title"] = ("Cài Đặt", "Settings"),
        ["Settings.Language"] = ("Ngôn ngữ giao diện", "Display Language"),
        ["Settings.Privilege"] = ("Quyền thực thi hiện tại", "Current Privilege Level")
    };

    public void SetLanguage(AppLanguage language)
    {
        if (CurrentLanguage == language) return;
        CurrentLanguage = language;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string key)
    {
        if (Table.TryGetValue(key, out var entry))
            return CurrentLanguage == AppLanguage.Vietnamese ? entry.Vi : entry.En;
        return key;
    }

    public string Pick(string vi, string en) => CurrentLanguage == AppLanguage.Vietnamese ? vi : en;
}
