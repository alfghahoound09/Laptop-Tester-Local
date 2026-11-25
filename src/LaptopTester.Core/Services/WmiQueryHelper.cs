using System.Management;

namespace LaptopTester.Core.Services;

/// <summary>Thread-safe, failure-tolerant async wrapper for Windows WMI/CIM queries.</summary>
public static class WmiQueryHelper
{
    public static Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(
        string query,
        string scopePath = @"root\cimv2",
        CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<Dictionary<string, object?>>>(() =>
        {
            var rows = new List<Dictionary<string, object?>>();
            try
            {
                var scope = new ManagementScope(scopePath);
                scope.Connect();
                var options = new System.Management.EnumerationOptions
                {
                    ReturnImmediately = true,
                    Rewindable = false,
                    Timeout = TimeSpan.FromSeconds(5)
                };
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query), options);
                using var results = searcher.Get();
                foreach (ManagementBaseObject obj in results)
                {
                    ct.ThrowIfCancellationRequested();
                    using (obj)
                    {
                        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (PropertyData prop in obj.Properties)
                            dict[prop.Name] = prop.Value;
                        rows.Add(dict);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // WMI providers (e.g. root\wmi battery/thermal on desktops or non-admin) can be absent or denied.
            }
            return rows;
        }, ct);
    }

    public static string Str(Dictionary<string, object?> row, string key, string fallback = "") =>
        row.TryGetValue(key, out var val) && val is not null ? val.ToString()?.Trim() ?? fallback : fallback;

    public static ulong ULong(Dictionary<string, object?> row, string key, ulong fallback = 0)
    {
        if (!row.TryGetValue(key, out var val) || val is null) return fallback;
        try { return Convert.ToUInt64(val); } catch { return fallback; }
    }

    public static int Int(Dictionary<string, object?> row, string key, int fallback = 0)
    {
        if (!row.TryGetValue(key, out var val) || val is null) return fallback;
        try { return Convert.ToInt32(val); } catch { return fallback; }
    }

    public static bool Bool(Dictionary<string, object?> row, string key, bool fallback = false)
    {
        if (!row.TryGetValue(key, out var val) || val is null) return fallback;
        try { return Convert.ToBoolean(val); } catch { return fallback; }
    }
}
