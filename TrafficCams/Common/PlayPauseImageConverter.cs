using System;
using Windows.UI.Xaml.Data;

namespace TrafficCams.Common
{
    class PlayPauseImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is Boolean)
                return (Boolean) value ? "ms-appx:///Assets/pause.png" : "ms-appx:///Assets/play.png";

            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
