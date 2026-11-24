using System;
using System.Globalization;
using System.Windows.Data;

namespace WarfAssistant.Controls
{
    /// <summary>
    /// Compares a string property against ConverterParameter (ordinal ignore case) for pill/tab radio bindings.
    /// </summary>
    public sealed class StringEqualsConverter : IValueConverter
    {
        public static readonly StringEqualsConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string? val = value as string;
            string? target = parameter as string;
            return string.Equals(val, target, StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    /// <summary>
    /// Returns "1" when strings match and "0" otherwise so Button.Tag DataTriggers avoid bool boxing quirks.
    /// </summary>
    public sealed class StringEqualsTagConverter : IValueConverter
    {
        public static readonly StringEqualsTagConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase) ? "1" : "0";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
