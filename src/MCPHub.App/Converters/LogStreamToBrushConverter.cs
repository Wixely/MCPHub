using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MCPHub.Core.Logging;

namespace MCPHub.App.Converters;

/// <summary>Colours a log line by its source stream (mid-tones chosen to read on light or dark themes).</summary>
public sealed class LogStreamToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogStream.Stderr => new SolidColorBrush(Color.Parse("#F0846B")), // soft red
        LogStream.Info => new SolidColorBrush(Color.Parse("#6AABFF")),   // soft blue
        _ => new SolidColorBrush(Color.Parse("#9DA5B4")),                // stdout / neutral grey
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
