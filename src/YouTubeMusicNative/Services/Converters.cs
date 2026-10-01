using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace YouTubeMusicNative.Services;

/// <summary>Seconds (double) → "m:ss" / "h:mm:ss".</summary>
public sealed class TimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var t = TimeSpan.FromSeconds(value is double d && d > 0 && double.IsFinite(d) ? d : 0);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Null / empty / false / 0 → Collapsed; anything else → Visible. ConverterParameter "invert" flips it.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool empty = value is null || value is string { Length: 0 } || value is false || value is 0;
        if (parameter is "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when any of the bound values is true.</summary>
public sealed class AnyTrueConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) => values.Any(v => v is true);
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Inverts a bool.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Picks a glyph from a bool: ConverterParameter "trueGlyph|falseGlyph".</summary>
public sealed class BoolToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "|").Split('|');
        return value is true ? parts[0] : parts[1];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>RepeatMode → repeat-all / repeat-one glyph.</summary>
public sealed class RepeatGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RepeatMode.One ? "" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>RepeatMode → true when repeat is on (for toggle highlighting).</summary>
public sealed class RepeatActiveConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RepeatMode.All or RepeatMode.One;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Volume 0–100 → speaker glyph with the matching number of waves.</summary>
public sealed class VolumeGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not double v || v <= 0 ? "" : v < 33 ? "" : v < 66 ? "" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Enum value → true if its name is one of the "|"-separated names in ConverterParameter.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && (parameter as string ?? "").Split('|').Contains(value.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>[a, b] → true when both are non-null and equal (e.g. row VideoId vs now-playing VideoId).</summary>
public sealed class EqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is not null && values[0] != DependencyProperty.UnsetValue && Equals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Color → frozen SolidColorBrush. Optional ConverterParameter = alpha 0–255.</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color c) return null;
        if (parameter is string s && byte.TryParse(s, out var a)) c.A = a;
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Index → 1-based display number (for AlternationIndex). ConverterParameter = offset (default 1).</summary>
public sealed class IndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? (i + (parameter is string s && int.TryParse(s, out var o) ? o : 1)).ToString() : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// [track, the page's track list, its count] → the row's 1-based number. Counted by reference (the same song can be
/// listed twice), and the count is only there so numbers refresh when rows are added or removed. Replaces
/// AlternationIndex, which WPF gets wrong (999967…) after a list is cleared and refilled.
/// </summary>
public sealed class TrackNumberConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [var item, System.Collections.IList list, ..] || item is null) return "";
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return (i + 1).ToString();
        return "";
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// [videoId, LikesVersion] → whether the song is liked. The version is only there so the binding
/// re-evaluates when likes change. ConverterParameter "a|b" returns a (liked) or b instead of a bool.
/// </summary>
public sealed class LikedConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool liked = values.Length > 0 && values[0] is string id && ViewModels.LibraryActions.Instance?.IsLiked(id) == true;
        if (parameter is not string p || p.Split('|') is not [var yes, var no]) return liked;
        var result = liked ? yes : no;
        // Colours ("#FF3355|#B3B3B3") for Foreground/Fill bindings.
        return typeof(Brush).IsAssignableFrom(targetType) ? new BrushConverter().ConvertFromString(result)! : result;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>[position, duration] → 0..1 (for progress lines drawn with a ScaleTransform).</summary>
public sealed class FractionConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [double pos, double dur, ..] && dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0.0;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
