using Microsoft.UI.Xaml.Data;

namespace ControlShift.App.Converters;

/// <summary>
/// Converts a battery level byte (0–3) to a human-readable string.
/// </summary>
public sealed class BatteryLevelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is byte level)
        {
            return level switch
            {
                0 => "Empty",
                1 => "Low",
                2 => "Medium",
                3 => "Full",
                _ => "Unknown"
            };
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
