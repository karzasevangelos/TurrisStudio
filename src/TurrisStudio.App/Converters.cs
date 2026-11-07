using System.Globalization;
using System.Windows.Data;

namespace TurrisStudio;

/// <summary>Binds an enum to a ComboBox SelectedIndex (enum values must be 0..n in item order).</summary>
public sealed class EnumIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Enum e ? System.Convert.ToInt32(e) : -1;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i >= 0 ? Enum.ToObject(targetType, i) : Binding.DoNothing;
}
