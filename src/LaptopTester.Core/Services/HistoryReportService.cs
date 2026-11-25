using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using LaptopTester.Core.Models;

namespace LaptopTester.Core.Services;

/// <summary>Persists test sessions in LocalAppData and exports self-contained HTML and JSON reports.</summary>
public sealed class HistoryReportService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string HistoryDirectory { get; }

    public HistoryReportService(string? customDirectory = null)
    {
        HistoryDirectory = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaptopTester",
            "history");
    }

    public async Task SaveSessionAsync(QuickTestSession session, CancellationToken ct = default)
    {
        Directory.CreateDirectory(HistoryDirectory);
        string path = Path.Combine(HistoryDirectory, $"{session.Timestamp:yyyyMMdd_HHmmss}_{session.Id}.json");
        string json = JsonSerializer.Serialize(session, JsonOpts);
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct);
    }

    public async Task<IReadOnlyList<QuickTestSession>> LoadSessionsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(HistoryDirectory))
            return Array.Empty<QuickTestSession>();

        var sessions = new List<QuickTestSession>();
        foreach (var file in Directory.GetFiles(HistoryDirectory, "*.json").OrderByDescending(f => f))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string json = await File.ReadAllTextAsync(file, Encoding.UTF8, ct);
                var s = JsonSerializer.Deserialize<QuickTestSession>(json, JsonOpts);
                if (s is not null) sessions.Add(s);
            }
            catch { }
        }
        return sessions;
    }

    public void DeleteSession(string sessionId)
    {
        if (!Directory.Exists(HistoryDirectory)) return;
        foreach (var file in Directory.GetFiles(HistoryDirectory, $"*_{sessionId}.json"))
        {
            try { File.Delete(file); } catch { }
        }
    }

    public void ClearAllHistory()
    {
        if (!Directory.Exists(HistoryDirectory)) return;
        foreach (var file in Directory.GetFiles(HistoryDirectory, "*.json"))
        {
            try { File.Delete(file); } catch { }
        }
    }

    public async Task ExportJsonAsync(string filePath, QuickTestSession session, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(session, JsonOpts);
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, ct);
    }

    public async Task ExportHtmlAsync(string filePath, QuickTestSession session, CancellationToken ct = default)
    {
        string verdictLabel = session.Verdict switch
        {
            VerdictBand.Good => "GOOD / PASS",
            VerdictBand.ConsiderCarefully => "CONSIDER CAREFULLY",
            _ => "DO NOT BUY / ISSUES FOUND"
        };
        string verdictColor = session.Verdict switch
        {
            VerdictBand.Good => "#1DB954",
            VerdictBand.ConsiderCarefully => "#F59E0B",
            _ => "#EF4444"
        };

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>LaptopTester Report - {WebUtility.HtmlEncode(session.MachineModel)}</title>");
        sb.AppendLine("<style>body{font-family:'Segoe UI',system-ui,sans-serif;background:#121212;color:#F3F4F6;margin:0;padding:32px;}");
        sb.AppendLine(".wrap{max-width:920px;margin:0 auto;}.card{background:#181818;border:1px solid #2A2A2A;border-radius:12px;padding:20px;margin-bottom:18px;}");
        sb.AppendLine("h1{margin:0 0 6px;font-size:24px;}h2{margin:0 0 12px;font-size:16px;color:#A1A1AA;text-transform:uppercase;letter-spacing:.05em;}");
        sb.AppendLine("table{width:100%;border-collapse:collapse;}th,td{text-align:left;padding:10px 8px;border-bottom:1px solid #262626;font-size:14px;}");
        sb.AppendLine(".badge{display:inline-block;padding:4px 10px;border-radius:999px;font-weight:600;font-size:12px;}");
        sb.AppendLine("@media print{body{background:#fff;color:#111;}.card{background:#fff;border-color:#ddd;}th,td{border-color:#ddd;}}</style></head><body><div class=\"wrap\">");
        sb.AppendLine($"<div class=\"card\"><h1>LaptopTester Diagnostic Report</h1>");
        sb.AppendLine($"<p style=\"color:#A1A1AA\">{WebUtility.HtmlEncode(session.MachineModel)} &middot; {session.Timestamp:yyyy-MM-dd HH:mm}</p>");
        sb.AppendLine($"<p><span class=\"badge\" style=\"background:{verdictColor}22;color:{verdictColor};border:1px solid {verdictColor}\">Score: {session.FinalScore}/100 &mdash; {verdictLabel}</span></p>");
        sb.AppendLine($"<p><strong>CPU:</strong> {WebUtility.HtmlEncode(session.CpuName)}<br><strong>RAM:</strong> {WebUtility.HtmlEncode(session.RamSummary)}<br><strong>Storage:</strong> {WebUtility.HtmlEncode(session.StorageSummary)}</p></div>");

        sb.AppendLine("<div class=\"card\"><h2>Test Results</h2><table><thead><tr><th>Test</th><th>Category</th><th>Status</th><th>Summary</th></tr></thead><tbody>");
        foreach (var r in session.Results)
        {
            string c = r.Status switch { TestStatus.Passed => "#22C55E", TestStatus.Warning => "#F59E0B", TestStatus.Failed => "#EF4444", _ => "#9CA3AF" };
            sb.AppendLine($"<tr><td>{WebUtility.HtmlEncode(r.Title)}</td><td>{WebUtility.HtmlEncode(r.Category)}</td><td style=\"color:{c};font-weight:600\">{r.Status}</td><td>{WebUtility.HtmlEncode(r.Summary)}</td></tr>");
        }
        sb.AppendLine("</tbody></table></div>");

        if (session.Deductions.Count > 0)
        {
            sb.AppendLine("<div class=\"card\"><h2>Score Deductions</h2><ul>");
            foreach (var d in session.Deductions)
                sb.AppendLine($"<li><strong>-{d.PointsDeducted} pts</strong> &mdash; {WebUtility.HtmlEncode(d.Title)}: {WebUtility.HtmlEncode(d.Reason)}</li>");
            sb.AppendLine("</ul></div>");
        }

        if (!string.IsNullOrWhiteSpace(session.Notes))
            sb.AppendLine($"<div class=\"card\"><h2>Notes</h2><p>{WebUtility.HtmlEncode(session.Notes)}</p></div>");

        sb.AppendLine("</div></body></html>");
        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8, ct);
    }
}
