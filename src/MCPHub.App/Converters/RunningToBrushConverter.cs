using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MCPHub.App.Converters;

/// <summary>Maps a running/stopped boolean to a green/grey status-dot brush.</summary>
public sealed class RunningToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.Parse("#3FB950"))   // green
            : new SolidColorBrush(Color.Parse("#8B949E"));  // grey

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
