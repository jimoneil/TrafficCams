using System;
using Windows.UI.Xaml.Controls;

//
// LICENSE: http://aka.ms/LicenseTerms-SampleApps
//

namespace TrafficCams.Flyouts
{
    public sealed partial class WebViewFlyout : UserControl
    {
        public WebViewFlyout(String htmlFile)
        {
            this.InitializeComponent();
            WebView.Loaded += (s, e) =>
            {
                WebView.Navigate(new System.Uri("ms-appx-web://" + htmlFile));
            };
        }
    }
}
