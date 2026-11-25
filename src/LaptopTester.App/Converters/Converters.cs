using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LaptopTester.Core.Models;

namespace LaptopTester.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasVal = value is string str ? !string.IsNullOrWhiteSpace(str) : value is not null;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            hasVal = !hasVal;
        return hasVal ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush PassBrush = Freeze("#22C55E");
    private static readonly SolidColorBrush WarnBrush = Freeze("#FACC15");
    private static readonly SolidColorBrush RunningBrush = Freeze("#FF853D");
    private static readonly SolidColorBrush FailBrush = Freeze("#EF4444");
    private static readonly SolidColorBrush NeutralBrush = Freeze("#8E8E9B");

    private static SolidColorBrush Freeze(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TestStatus ts)
        {
            return ts switch
            {
                TestStatus.Passed => PassBrush,
                TestStatus.Warning => WarnBrush,
                TestStatus.Running => RunningBrush,
                TestStatus.Failed => FailBrush,
                _ => NeutralBrush
            };
        }
        if (value is bool ok)
            return ok ? PassBrush : FailBrush;
        if (value is SpoofingRisk risk)
        {
            return risk switch
            {
                SpoofingRisk.None => PassBrush,
                SpoofingRisk.Low => WarnBrush,
                _ => FailBrush
            };
        }
        return NeutralBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StatusToBgBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush PassBg = Freeze("#0F2618");
    private static readonly SolidColorBrush WarnBg = Freeze("#29210A");
    private static readonly SolidColorBrush RunningBg = Freeze("#241208");
    private static readonly SolidColorBrush FailBg = Freeze("#2C1215");
    private static readonly SolidColorBrush NeutralBg = Freeze("#1A1A22");

    private static SolidColorBrush Freeze(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TestStatus ts)
        {
            return ts switch
            {
                TestStatus.Passed => PassBg,
                TestStatus.Warning => WarnBg,
                TestStatus.Running => RunningBg,
                TestStatus.Failed => FailBg,
                _ => NeutralBg
            };
        }
        if (value is bool ok)
            return ok ? PassBg : FailBg;
        return NeutralBg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class VerdictToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GoodBrush = Freeze("#22C55E");
    private static readonly SolidColorBrush ConsiderBrush = Freeze("#FACC15");
    private static readonly SolidColorBrush BadBrush = Freeze("#EF4444");
    private static readonly SolidColorBrush DefaultBrush = Freeze("#FF853D");

    private static SolidColorBrush Freeze(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is VerdictBand band
            ? band switch
            {
                VerdictBand.Good => GoodBrush,
                VerdictBand.ConsiderCarefully => ConsiderBrush,
                VerdictBand.DoNotBuy => BadBrush,
                _ => DefaultBrush
            }
            : DefaultBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class VerdictToBgBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GoodBg = Freeze("#0F2618");
    private static readonly SolidColorBrush ConsiderBg = Freeze("#29210A");
    private static readonly SolidColorBrush BadBg = Freeze("#2C1215");
    private static readonly SolidColorBrush DefaultBg = Freeze("#241208");

    private static SolidColorBrush Freeze(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is VerdictBand band
            ? band switch
            {
                VerdictBand.Good => GoodBg,
                VerdictBand.ConsiderCarefully => ConsiderBg,
                VerdictBand.DoNotBuy => BadBg,
                _ => DefaultBg
            }
            : DefaultBg;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return Visibility.Collapsed;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class PositiveIntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ZeroIntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
