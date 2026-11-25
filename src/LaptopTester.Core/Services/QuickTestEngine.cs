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

    /// <summary>Default variable round queue ordering critical hardware/storage/battery checks ahead of audio and human inputs.</summary>
    public static readonly IReadOnlyList<QuickTestRound> DefaultHybridRounds =
    [
        new("hardware", "Hardware & Authenticity Check", 1, 18, QuickTestRoundKind.Automated, IsCriticalFail: true),
        new("storage", "Storage & Drive Health (SMART)", 2, 14, QuickTestRoundKind.Automated, IsCriticalFail: true),
        new("battery", "Battery Health & Real Capacity", 3, 14, QuickTestRoundKind.Automated, IsCriticalFail: false),
        new("connectivity", "USB, Wi-Fi & Bluetooth", 4, 6, QuickTestRoundKind.Automated, IsCriticalFail: false),
        new("camera", "Webcam Preview", 5, 6, QuickTestRoundKind.Automated, IsCriticalFail: false),
        new("speaker", "Stereo Speakers", 6, 6, QuickTestRoundKind.HybridAuto, IsCriticalFail: false),
        new("microphone", "Microphone Recording", 7, 6, QuickTestRoundKind.HybridAuto, IsCriticalFail: false),
        new("display", "Screen & Dead Pixel Check", 8, 12, QuickTestRoundKind.Interactive, IsCriticalFail: true),
        new("touchpad", "Touchpad & Pointer", 9, 8, QuickTestRoundKind.Interactive, IsCriticalFail: false),
        new("keyboard", "Keyboard Key Test", 10, 10, QuickTestRoundKind.Interactive, IsCriticalFail: false)
    ];

    /// <summary>Builds a fresh PriorityQueue of diagnostic rounds so each run can vary priorities or enabled rounds.</summary>
    public static PriorityQueue<QuickTestRound, int> BuildRoundQueue(IEnumerable<QuickTestRound>? customRounds = null)
    {
        var queue = new PriorityQueue<QuickTestRound, int>();
        foreach (var round in customRounds ?? DefaultHybridRounds)
            queue.Enqueue(round, round.Priority);
        return queue;
    }

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
