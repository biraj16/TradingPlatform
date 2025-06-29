using System;
using System.Globalization;
using System.Windows.Data;

namespace TradingConsole.Wpf.Converters
{
    public class ValueToSignConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // This converter checks if a numeric value is positive, negative, or zero.
            // It's used by the XAML Style to apply the correct color (green for positive, red for negative).
            if (value is decimal decValue)
            {
                if (decValue > 0) return "Positive";
                if (decValue < 0) return "Negative";
            }

            if (value is double dblValue)
            {
                if (dblValue > 0) return "Positive";
                if (dblValue < 0) return "Negative";
            }

            if (value is int intValue)
            {
                if (intValue > 0) return "Positive";
                if (intValue < 0) return "Negative";
            }

            return "Zero";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // This method is not needed for our one-way binding.
            throw new NotImplementedException();
        }
    }
}
