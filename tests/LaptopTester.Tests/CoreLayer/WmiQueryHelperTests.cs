using LaptopTester.Core.Services;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class WmiQueryHelperTests
{
    [Fact]
    public void TypedExtractors_HandleValidNullMissingAndInvalidValues()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["StrVal"] = "  hello  ",
            ["NullVal"] = null,
            ["UIntVal"] = 42u,
            ["StrNum"] = "123",
            ["Garbage"] = "not-a-number",
            ["BoolTrue"] = "true",
            ["BoolInt"] = 1
        };

        Assert.Equal("hello", WmiQueryHelper.Str(row, "StrVal"));
        Assert.Equal("fb", WmiQueryHelper.Str(row, "NullVal", "fb"));
        Assert.Equal("fb", WmiQueryHelper.Str(row, "Missing", "fb"));

        Assert.Equal(42UL, WmiQueryHelper.ULong(row, "UIntVal"));
        Assert.Equal(123UL, WmiQueryHelper.ULong(row, "StrNum"));
        Assert.Equal(9UL, WmiQueryHelper.ULong(row, "Garbage", 9UL));

        Assert.Equal(123, WmiQueryHelper.Int(row, "StrNum"));
        Assert.Equal(-1, WmiQueryHelper.Int(row, "Garbage", -1));

        Assert.True(WmiQueryHelper.Bool(row, "BoolTrue"));
        Assert.True(WmiQueryHelper.Bool(row, "BoolInt"));
        Assert.False(WmiQueryHelper.Bool(row, "Garbage"));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task QueryAsync_HandlesValidInvalidAndCancelledQueries()
    {
        var osRows = await WmiQueryHelper.QueryAsync("SELECT Caption FROM Win32_OperatingSystem");
        Assert.Single(osRows);
        Assert.False(string.IsNullOrWhiteSpace(WmiQueryHelper.Str(osRows[0], "Caption")));

        var missingClass = await WmiQueryHelper.QueryAsync("SELECT * FROM Win32_NonExistentClassXYZ");
        Assert.Empty(missingClass);

        var badScope = await WmiQueryHelper.QueryAsync("SELECT * FROM Win32_OperatingSystem", @"root\doesnotexist");
        Assert.Empty(badScope);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WmiQueryHelper.QueryAsync("SELECT Caption FROM Win32_OperatingSystem", ct: cts.Token));
    }
}
