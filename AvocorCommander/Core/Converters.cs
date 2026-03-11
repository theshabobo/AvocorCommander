using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AvocorCommander.Core;

[ValueConversion(typeof(bool), typeof(SolidColorBrush))]
public sealed class BoolToColorBrushConverter : IValueConverter
{
    public object TrueColor  { get; set; } = new SolidColorBrush(Color.FromRgb(0x16, 0xC0, 0x80));
    public object FalseColor { get; set; } = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));

    public object Convert(object value, Type _, object __, CultureInfo ___)
        => value is true ? TrueColor : FalseColor;
    public object ConvertBack(object value, Type _, object __, CultureInfo ___)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type _, object __, CultureInfo ___)
    {
        bool b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type _, object __, CultureInfo ___)
        => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type _, object __, CultureInfo ___)
    {
        bool hasValue = value != null && value.ToString() != string.Empty;
        if (Invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type _, object __, CultureInfo ___)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(string), typeof(SolidColorBrush))]
public sealed class HexColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type _, object __, CultureInfo ___)
    {
        try
        {
            if (value is string hex)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch { }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type _, object __, CultureInfo ___)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type _, object __, CultureInfo ___)
        => value is bool b ? !b : value;
    public object ConvertBack(object value, Type _, object __, CultureInfo ___)
        => value is bool b ? !b : value;
}

/// <summary>Equal-to-parameter → true (for RadioButton-style nav buttons).</summary>
public sealed class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type _, object parameter, CultureInfo ___)
        => value?.ToString() == parameter?.ToString();
    public object ConvertBack(object value, Type _, object parameter, CultureInfo ___)
        => value is true ? parameter : Binding.DoNothing;
}

/// <summary>Converts zero → Collapsed, non-zero → Visible.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type _, object __, CultureInfo ___)
    {
        bool hasItems = value is int i && i > 0;
        if (Invert) hasItems = !hasItems;
        return hasItems ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type _, object __, CultureInfo ___)
        => throw new NotSupportedException();
}
