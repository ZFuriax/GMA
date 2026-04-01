using System;
using System.Globalization;
using System.Windows.Data;

namespace MusicPlayer
{
    public sealed class MultiplyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double d &&
                parameter != null &&
                double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mul))
            {
                return d * mul;
            }

            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}