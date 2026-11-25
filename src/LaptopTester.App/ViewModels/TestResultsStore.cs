using System.Collections.ObjectModel;
using LaptopTester.Core.Models;

namespace LaptopTester.App.ViewModels;

/// <summary>Shared store holding the latest diagnostic result for each test category.</summary>
public sealed class TestResultsStore
{
    public ObservableCollection<TestResultEntry> Results { get; } = new();
    public event EventHandler? ResultsChanged;

    public void Upsert(string testId, string title, string category, TestStatus status, string summary)
    {
        var existing = Results.FirstOrDefault(r => r.TestId.Equals(testId, StringComparison.OrdinalIgnoreCase));
        var entry = new TestResultEntry(testId, title, category, status, summary, 0, DateTimeOffset.Now);
        if (existing is not null)
        {
            int idx = Results.IndexOf(existing);
            Results[idx] = entry;
        }
        else
        {
            Results.Add(entry);
        }
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    public TestResultEntry? Get(string testId) =>
        Results.FirstOrDefault(r => r.TestId.Equals(testId, StringComparison.OrdinalIgnoreCase));

    public void Clear()
    {
        Results.Clear();
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }
}
