using LaptopTester.Core.Models;

namespace LaptopTester.Core.Services;

/// <summary>Computes the 100-point Quick Test score, deductions, and final purchase verdict.</summary>
public sealed class QuickTestEngine
{
    public static readonly IReadOnlyList<(string Id, string Title, int Weight)> StandardSteps =
    [
        ("hardware", "Hardware & Authenticity Check", 18),
        ("storage", "Storage & Drive Health (SMART)", 14),
        ("battery", "Battery Health & Real Capacity", 14),
        ("display", "Screen & Dead Pixel Check", 12),
        ("keyboard", "Keyboard Key Test", 10),
        ("touchpad", "Touchpad & Pointer", 8),
        ("speaker", "Stereo Speakers", 6),
        ("microphone", "Microphone Recording", 6),
        ("camera", "Webcam Preview", 6),
        ("connectivity", "USB, Wi-Fi & Bluetooth", 6)
    ];

    public (int FinalScore, VerdictBand Verdict, IReadOnlyList<ScoreDeduction> Deductions) Evaluate(IEnumerable<TestResultEntry> results)
    {
        var map = results.GroupBy(r => r.TestId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase);

        int score = 100;
        var deductions = new List<ScoreDeduction>();
        bool hasCriticalFail = false;

        foreach (var (id, _, weight) in StandardSteps)
        {
            if (!map.TryGetValue(id, out var entry)) continue;

            if (entry.Status == TestStatus.Failed)
            {
                score -= weight;
                deductions.Add(new ScoreDeduction(id, entry.Title, weight, string.IsNullOrWhiteSpace(entry.Summary) ? "Failed check" : entry.Summary));
                if (id is "hardware" or "storage" or "display")
                    hasCriticalFail = true;
            }
            else if (entry.Status == TestStatus.Warning)
            {
                int half = Math.Max(2, weight / 2);
                score -= half;
                deductions.Add(new ScoreDeduction(id, entry.Title, half, string.IsNullOrWhiteSpace(entry.Summary) ? "Warning" : entry.Summary));
            }
        }

        score = Math.Clamp(score, 0, 100);
        VerdictBand verdict = (hasCriticalFail || score < 60)
            ? VerdictBand.DoNotBuy
            : score < 82 || deductions.Count >= 2
                ? VerdictBand.ConsiderCarefully
                : VerdictBand.Good;

        return (score, verdict, deductions);
    }
}
