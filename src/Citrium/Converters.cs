using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Citrium;

public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        Invert ? value is not Visibility.Visible : value is Visibility.Visible;
}
