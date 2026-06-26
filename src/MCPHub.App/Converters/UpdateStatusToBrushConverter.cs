using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MCPHub.Core.Models;

namespace MCPHub.App.Converters;

/// <summary>Maps an <see cref="UpdateStatus"/> to a status-dot brush for the services list.</summary>
public sealed class UpdateStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        UpdateStatus.UpdateAvailable => new SolidColorBrush(Color.Parse("#E0A800")), // amber
        UpdateStatus.UpToDate => new SolidColorBrush(Color.Parse("#3FB950")),        // green
        UpdateStatus.NotInstalled => new SolidColorBrush(Color.Parse("#8B949E")),    // grey
        _ => new SolidColorBrush(Color.Parse("#8B949E")),                            // Unknown
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
