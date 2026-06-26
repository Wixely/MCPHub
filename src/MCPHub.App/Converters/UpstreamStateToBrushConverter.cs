using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MCPHub.Proxy;

namespace MCPHub.App.Converters;

/// <summary>Maps an upstream connection state to a status-dot brush.</summary>
public sealed class UpstreamStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        UpstreamState.Connected => new SolidColorBrush(Color.Parse("#3FB950")),   // green
        UpstreamState.Connecting => new SolidColorBrush(Color.Parse("#E0A800")),  // amber
        UpstreamState.Faulted => new SolidColorBrush(Color.Parse("#F85149")),     // red
        _ => new SolidColorBrush(Color.Parse("#8B949E")),                         // grey
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
