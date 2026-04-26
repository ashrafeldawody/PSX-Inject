using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PsxInject.Models;
using PsxInject.Server;

namespace PsxInject.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool bv && bv;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || (value is string s && string.IsNullOrEmpty(s))
            ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class RequestKindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RequestKind k) return Brushes.Gray;

        var key = k switch
        {
            RequestKind.CacheHit  => "B.Success",
            RequestKind.CacheMiss => "B.Warning",
            RequestKind.Proxy     => "B.Info",
            RequestKind.Fallback  => "B.AccentBright",
            RequestKind.Error     => "B.Error",
            _ => "B.TextDim"
        };

        if (Application.Current?.Resources[key] is Brush b) return b;
        return Brushes.Gray;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class LongToFormattedBytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is long l ? FormatHelpers.FormatBytes(l) : "0 B";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class IntEqualsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        if (!TryAsInt(value, out var v)) return false;
        if (!int.TryParse(parameter.ToString(), out var p)) return false;
        return v == p;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null && int.TryParse(parameter.ToString(), out var p))
        {
            if (targetType.IsEnum) return Enum.ToObject(targetType, p);
            return p;
        }
        return Binding.DoNothing;
    }

    private static bool TryAsInt(object value, out int result)
    {
        if (value is Enum en) { result = System.Convert.ToInt32(en); return true; }
        return int.TryParse(value.ToString(), out result);
    }
}

public class IntEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return Visibility.Collapsed;
        if (value is Enum en)
        {
            if (!int.TryParse(parameter.ToString(), out var pe)) return Visibility.Collapsed;
            return System.Convert.ToInt32(en) == pe ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!int.TryParse(value.ToString(), out var v)) return Visibility.Collapsed;
        if (!int.TryParse(parameter.ToString(), out var p)) return Visibility.Collapsed;
        return v == p ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
