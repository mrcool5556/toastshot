using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Toastshot;

/// <summary>Attached properties the control templates read.</summary>
public static class Ui
{
    /// <summary>Corner radius of a button's chrome (6 for buttons, 10 for launcher tiles).</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);

    /// <summary>The shortcut letter a drawing tool shows after its name.</summary>
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));

    public static string GetKey(DependencyObject element) => (string)element.GetValue(KeyProperty);

    public static void SetKey(DependencyObject element, string value) => element.SetValue(KeyProperty, value);
}

/// <summary>"Ctrl+Alt+S" → ["Ctrl", "Alt", "S"], one keycap per key.</summary>
public sealed class KeycapsConverter : IValueConverter
{
    public static string[] Split(string? gesture) =>
        string.IsNullOrWhiteSpace(gesture)
            ? []
            : gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(key => key == "PrintScreen" ? "PrtSc" : key) // as printed on the key
                .ToArray();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Split(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
