using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MCPHub.Core.Models;

namespace MCPHub.App.Converters;

/// <summary>Maps a <see cref="ServiceRunState"/> to a status-badge brush.</summary>
public sealed class RunStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ServiceRunState.Running => new SolidColorBrush(Color.Parse("#3FB950")),    // green
        ServiceRunState.Starting => new SolidColorBrush(Color.Parse("#E0A800")),   // amber
        ServiceRunState.Stopping => new SolidColorBrush(Color.Parse("#E0A800")),   // amber
        ServiceRunState.Unhealthy => new SolidColorBrush(Color.Parse("#DB6D28")),  // orange
        ServiceRunState.Faulted => new SolidColorBrush(Color.Parse("#F85149")),    // red
        _ => new SolidColorBrush(Color.Parse("#8B949E")),                          // Stopped / grey
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
