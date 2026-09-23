using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MCPHub.App.Converters;

/// <summary>
/// Maps a pass / fail / not-yet-run test outcome to a text brush. Null is the "still running or never run"
/// case and stays neutral, so a row in progress does not flash green or red.
/// </summary>
public sealed class TestResultToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => new SolidColorBrush(Color.Parse("#3FB950")),   // green
        false => new SolidColorBrush(Color.Parse("#F85149")),  // red
        _ => new SolidColorBrush(Color.Parse("#8B949E")),      // grey
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
