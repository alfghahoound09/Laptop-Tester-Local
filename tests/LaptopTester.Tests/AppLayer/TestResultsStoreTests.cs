using LaptopTester.App.ViewModels;
using LaptopTester.Core.Models;
using Xunit;

namespace LaptopTester.Tests.AppLayer;

public sealed class TestResultsStoreTests
{
    [Fact]
    public void UpsertGetAndClear_ManageEntriesCaseInsensitivelyAndNotify()
    {
        var store = new TestResultsStore();
        int changes = 0;
        store.ResultsChanged += (_, _) => changes++;

        store.Upsert("hardware", "Hardware", "Core", TestStatus.Passed, "First");
        Assert.Single(store.Results);
        Assert.Equal(1, changes);

        store.Upsert("HARDWARE", "Hardware", "Core", TestStatus.Failed, "Updated");
        Assert.Single(store.Results);
        Assert.Equal(2, changes);

        var fetched = store.Get("HaRdWaRe");
        Assert.NotNull(fetched);
        Assert.Equal(TestStatus.Failed, fetched!.Status);
        Assert.Equal("Updated", fetched.Summary);

        store.Clear();
        Assert.Empty(store.Results);
        Assert.Null(store.Get("hardware"));
        Assert.Equal(3, changes);
    }
}
