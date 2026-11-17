using System;
using System.Globalization;

namespace FreeVideoStudio.App;

public sealed class BoolToOpacityConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double on = 1.0;
        if (parameter is string s &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            on = Math.Clamp(parsed, 0.0, 1.0);
        }

        return value is bool b && b ? on : 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
