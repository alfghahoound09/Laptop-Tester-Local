using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LaptopTester.App.Converters;
using LaptopTester.Core.Models;
using Xunit;

namespace LaptopTester.Tests.AppLayer;

public sealed class ConvertersTests
{
    private static string Hex(object brushObj)
    {
        var brush = Assert.IsType<SolidColorBrush>(brushObj);
        return $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
    }

    [Fact]
    public void BoolToVisibilityConverter_HandlesTrueFalseNullAndInvert()
    {
        var conv = new BoolToVisibilityConverter();
        Assert.Equal(Visibility.Visible, conv.Convert(true, typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, conv.Convert(false, typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, conv.Convert(null!, typeof(Visibility), null!, CultureInfo.InvariantCulture));

        Assert.Equal(Visibility.Collapsed, conv.Convert(true, typeof(Visibility), "invert", CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, conv.Convert(false, typeof(Visibility), "invert", CultureInfo.InvariantCulture));
        Assert.Equal(true, conv.ConvertBack(Visibility.Visible, typeof(bool), null!, CultureInfo.InvariantCulture));
        Assert.Equal(false, conv.ConvertBack(Visibility.Collapsed, typeof(bool), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void InverseBoolConverter_InvertsBooleans()
    {
        var conv = new InverseBoolConverter();
        Assert.Equal(false, conv.Convert(true, typeof(bool), null!, CultureInfo.InvariantCulture));
        Assert.Equal(true, conv.Convert(false, typeof(bool), null!, CultureInfo.InvariantCulture));
        Assert.Equal(true, conv.Convert(null!, typeof(bool), null!, CultureInfo.InvariantCulture));
        Assert.Equal(false, conv.ConvertBack(true, typeof(bool), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NullToVisibilityConverter_HandlesStringsObjectsAndInvert()
    {
        var conv = new NullToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, conv.Convert(null!, typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, conv.Convert("   ", typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, conv.Convert("ok", typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, conv.Convert(new object(), typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, conv.Convert(null!, typeof(Visibility), "invert", CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, conv.Convert(new object(), typeof(Visibility), "invert", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(TestStatus.Passed, "#22C55E", "#0F2618")]
    [InlineData(TestStatus.Warning, "#FACC15", "#29210A")]
    [InlineData(TestStatus.Running, "#FF853D", "#241208")]
    [InlineData(TestStatus.Failed, "#EF4444", "#2C1215")]
    [InlineData(TestStatus.Pending, "#8E8E9B", "#1A1A22")]
    [InlineData(TestStatus.Skipped, "#8E8E9B", "#1A1A22")]
    public void StatusConverters_ReturnExpectedColors(TestStatus status, string fgHex, string bgHex)
    {
        var fg = new StatusToBrushConverter();
        var bg = new StatusToBgBrushConverter();

        Assert.Equal(fgHex, Hex(fg.Convert(status, typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal(bgHex, Hex(bg.Convert(status, typeof(Brush), null!, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void StatusConverters_HandleBoolSpoofingRiskAndFallback()
    {
        var fg = new StatusToBrushConverter();
        var bg = new StatusToBgBrushConverter();

        Assert.Equal("#22C55E", Hex(fg.Convert(true, typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal("#EF4444", Hex(fg.Convert(false, typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal("#0F2618", Hex(bg.Convert(true, typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal("#2C1215", Hex(bg.Convert(false, typeof(Brush), null!, CultureInfo.InvariantCulture)));

        Assert.Equal("#22C55E", Hex(fg.Convert(SpoofingRisk.None, typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal("#FACC15", Hex(fg.Convert(SpoofingRisk.Low, typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal("#EF4444", Hex(fg.Convert(SpoofingRisk.Medium, typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal("#EF4444", Hex(fg.Convert(SpoofingRisk.High, typeof(Brush), null!, CultureInfo.InvariantCulture)));

        Assert.Equal("#8E8E9B", Hex(fg.Convert("other", typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal("#1A1A22", Hex(bg.Convert("other", typeof(Brush), null!, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(VerdictBand.Good, "#22C55E", "#0F2618")]
    [InlineData(VerdictBand.ConsiderCarefully, "#FACC15", "#29210A")]
    [InlineData(VerdictBand.DoNotBuy, "#EF4444", "#2C1215")]
    public void VerdictConverters_ReturnExpectedColors(VerdictBand band, string fgHex, string bgHex)
    {
        var fg = new VerdictToBrushConverter();
        var bg = new VerdictToBgBrushConverter();

        Assert.Equal(fgHex, Hex(fg.Convert(band, typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal(bgHex, Hex(bg.Convert(band, typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal("#FF853D", Hex(fg.Convert("bad", typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal("#241208", Hex(bg.Convert("bad", typeof(Brush), null!, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void EqualityAndIntVisibilityConverters_WorkCorrectly()
    {
        var eqBool = new EqualsToBoolConverter();
        var eqVis = new EqualsToVisibilityConverter();
        var posVis = new PositiveIntToVisibilityConverter();
        var zeroVis = new ZeroIntToVisibilityConverter();

        Assert.Equal(true, eqBool.Convert(TestStatus.Passed, typeof(bool), "passed", CultureInfo.InvariantCulture));
        Assert.Equal(false, eqBool.Convert(TestStatus.Failed, typeof(bool), "passed", CultureInfo.InvariantCulture));
        Assert.Equal(false, eqBool.Convert(null!, typeof(bool), "passed", CultureInfo.InvariantCulture));

        Assert.Equal(Visibility.Visible, eqVis.Convert(TestStatus.Passed, typeof(Visibility), "Passed", CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, eqVis.Convert(TestStatus.Failed, typeof(Visibility), "Passed", CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, eqVis.Convert(null!, typeof(Visibility), "Passed", CultureInfo.InvariantCulture));

        Assert.Equal(Visibility.Visible, posVis.Convert(3, typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, posVis.Convert(0, typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, posVis.Convert("3", typeof(Visibility), null!, CultureInfo.InvariantCulture));

        Assert.Equal(Visibility.Visible, zeroVis.Convert(0, typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, zeroVis.Convert(2, typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, zeroVis.Convert("0", typeof(Visibility), null!, CultureInfo.InvariantCulture));
    }
}
