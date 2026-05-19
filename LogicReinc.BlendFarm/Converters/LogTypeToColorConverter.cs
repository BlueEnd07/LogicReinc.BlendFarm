using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;
using LogicReinc.BlendFarm.Windows;

namespace LogicReinc.BlendFarm.Converters
{
    public class LogTypeToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ActivityLogType logType)
            {
                return logType switch
                {
                    ActivityLogType.Success => new SolidColorBrush(Color.Parse("#12C979")),
                    ActivityLogType.Warning => new SolidColorBrush(Color.Parse("#F59E0B")),
                    ActivityLogType.Error => new SolidColorBrush(Color.Parse("#EF4444")),
                    _ => new SolidColorBrush(Color.Parse("#D9DEF0"))
                };
            }
            return new SolidColorBrush(Color.Parse("#D9DEF0"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
