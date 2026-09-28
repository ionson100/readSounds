using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace readSounds.Views;

/// <summary>Цвет метки состояния проекта в списке.</summary>
public class StatusKindToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush IdleBrush = new(Color.Parse("#9AA0A6"));

    private static readonly SolidColorBrush RunningBrush = new(Color.Parse("#1A73E8"));

    private static readonly SolidColorBrush DoneBrush = new(Color.Parse("#1E8E3E"));

    private static readonly SolidColorBrush FailedBrush = new(Color.Parse("#D93025"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "inprogress" => RunningBrush,
            "done" => DoneBrush,
            "failed" => FailedBrush,
            _ => IdleBrush,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
