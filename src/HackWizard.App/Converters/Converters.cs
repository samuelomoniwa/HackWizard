using System.Globalization;
using System.Windows;
using System.Windows.Data;
using HackWizard.Core.Preflight;
using HackWizard.Core.Rules;

namespace HackWizard.App.Converters;

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            CompatStatus.Supported or CheckStatus.Pass => "Brush.Good",
            CompatStatus.Partial or CheckStatus.Warn => "Brush.Warn",
            CompatStatus.Unsupported or CheckStatus.Fail => "Brush.Bad",
            _ => "Brush.Muted"
        };
        return Application.Current.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CompatStatus.Supported or CheckStatus.Pass => "● Works",
        CompatStatus.Partial or CheckStatus.Warn => "▲ Partial",
        CompatStatus.Unsupported or CheckStatus.Fail => "✖ No",
        _ => "ℹ Info"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } || value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
