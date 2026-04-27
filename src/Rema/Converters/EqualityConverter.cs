using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Rema.Converters;

public class EqualityConverter : IValueConverter
{
    public static readonly EqualityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter is string paramStr && int.TryParse(paramStr, out var paramInt))
            return intValue == paramInt;
        return value?.Equals(parameter) ?? parameter is null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
