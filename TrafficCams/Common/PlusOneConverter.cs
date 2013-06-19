using System;
using Windows.UI.Xaml.Data;

namespace TrafficCams.Common
{
    class PlusOneConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is Int32)
                return ((Int32)value) + 1;

            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
