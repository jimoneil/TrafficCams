using APIMASH.Mapping;
using TrafficCams.Common;
using TrafficCams.Mapping;
using Bing.Maps;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Windows.ApplicationModel.Search;
using Windows.Devices.Geolocation;
using Windows.Storage;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Navigation;
using Windows.UI.ViewManagement;
using Windows.ApplicationModel.DataTransfer;

//
// LICENSE: http://aka.ms/LicenseTerms-SampleApps
//

namespace TrafficCams
{
    /// <summary>
    /// Main page of application displaying a map and a "LeftPanel" object. All code in this class is indepedent of the
    /// specific API used to populate point-of-interest on the maps (such as TomTom traffic cameras for this sample).
    /// </summary>
    public sealed partial class MainPage : LayoutAwarePage
    {
        /// <summary>
        /// State of current page saved when app is suspended
        /// </summary>
        [DataContract]
        public class MainPageState
        {
            /// <summary>
            /// Center point of the map view
            /// </summary>
            [DataMember]
            public LatLong MapCenter { get; set; }

            /// <summary>
            /// Zoom level of map view
            /// </summary>
            [DataMember]
            public double Zoom { get; set; }

            /// <summary>
            /// Boundaries of the map when the last refresh occurred
            /// </summary>
            [DataMember]
            public BoundingBox MapBox { get; set; }

            /// <summary>
            /// Id of IMappable item that was last selected
            /// </summary>
            [DataMember]
            public String SelectedItemId { get; set; }

            /// <summary>
            /// whether there is a pending refresh on the map
            /// </summary>
            [DataMember]
            public Boolean PendingRefresh { get; set; }
        }
        MainPageState _pageState = new MainPageState();

        Geolocator _geolocator;
        CurrentLocationPin _locationMarker = new CurrentLocationPin();
        Boolean _firstRun; 

        // state variables to detect when a map view refresh should be prompted
        ApplicationViewState _priorOrientation;
        Boolean _noRefreshRequiredViewChange;
        Boolean _retainRefreshRequiredViewChange;
        BoundingBox _lastBox;

        public MainPage()
        {
            this.InitializeComponent();

            // view model
            this.DefaultViewModel["PendingRefresh"] = false;

            // check to see if this is the first time application is being executed by checking for data in local settings.
            // After checking add some notional data as marker for next time app is run. This will be used to determine whether
            // to prompt the user (or not) that location services are not turned on for the app/system. Without this check, the
            // first time the app is run, it will provide a system prompt, and if that prompt is dismissed without granting 
            // access the propmpt displayed by the application would also appear unnecessarily.
            _firstRun = ApplicationData.Current.LocalSettings.Values.Count == 0;
            if (_firstRun)
                ApplicationData.Current.LocalSettings.Values.Add(
                    new System.Collections.Generic.KeyValuePair<string, object>("InitialRunDate", DateTime.UtcNow.ToString()));

            this.SizeChanged += (s, e) =>
            {
                // determine if there's been a change in orientation that doesn't require notice that the map view has changed (e.g., app open, snapped mode transitions)              
                if (_priorOrientation == ApplicationView.Value) return;

                _noRefreshRequiredViewChange = (_priorOrientation == ApplicationViewState.Snapped || ApplicationView.Value == ApplicationViewState.Snapped);
                _priorOrientation = ApplicationView.Value;

                VisualStateManager.GoToState(LeftPanel, ApplicationView.Value.ToString(), true);
            };

            // whenever map view changes track center point and zoom level in page state
            TheMap.ViewChangeEnded += (s, e) =>
                {
                    // save new center/zoom for page state
                    _pageState.MapCenter = new LatLong(TheMap.TargetCenter.Latitude, TheMap.TargetCenter.Longitude);
                    _pageState.Zoom = TheMap.TargetZoomLevel;

                    // ViewChangeEnded fires a bit too often, so retain bounding box to determine if the view truly has changed
                    BoundingBox thisBox = new BoundingBox(TheMap.TargetBounds.North, TheMap.TargetBounds.South,
                                      TheMap.TargetBounds.West, TheMap.TargetBounds.East);

                    // determine if view change should notify user of pending refresh requirement
                    if (_retainRefreshRequiredViewChange)
                        this.DefaultViewModel["PendingRefresh"] = _pageState.PendingRefresh;
                    else if (_noRefreshRequiredViewChange)
                        this.DefaultViewModel["PendingRefresh"] = false;
                    else if (App.InitialMapResizeHasOccurred)
                        this.DefaultViewModel["PendingRefresh"] = (thisBox != _lastBox);

                    // update state variables
                    _lastBox = thisBox;
                    if (App.InitialMapResizeHasOccurred) _noRefreshRequiredViewChange = false;
                    _retainRefreshRequiredViewChange = false;
                    App.InitialMapResizeHasOccurred = true;
                };

            // if refresh prompt is tapped, refresh the map
            RefreshPrompt.Tapped += async (s, e) =>
                {
                    await LeftPanel.Refresh(_lastBox);
                    this.DefaultViewModel["PendingRefresh"] = false;
                };

            // a tap on map will cause refresh is one is predicated
            TheMap.Tapped += async (s, e) =>
                {
                    if ((Boolean)this.DefaultViewModel["PendingRefresh"])
                    {
                        await LeftPanel.Refresh(_lastBox);
                        this.DefaultViewModel["PendingRefresh"] = false;
                    }
                };

            // set the reference to the current map for the LeftPanel (note: using Element binding will not handle all of the page navigation scenarios)
            LeftPanel.Map = TheMap;

            // whenever the contents of left panel are refreshed, save the map coordinates that were in play as part of the page state
            LeftPanel.Refreshed += (s, e) =>
                {
                    _pageState.MapBox = new BoundingBox(TheMap.TargetBounds.North, TheMap.TargetBounds.South, TheMap.TargetBounds.West, TheMap.TargetBounds.East);
                };

            // whenver a new item is selected in the left panel, update the map pins and save the item selected as part of the page state
            LeftPanel.ItemSelected += (s, e) =>
                {
                    TheMap.HighlightPointOfInterestPin(e.NewItem, true);
                    TheMap.HighlightPointOfInterestPin(e.OldItem, false);

                    this._pageState.SelectedItemId = e.NewItem == null ? null : e.NewItem.Id;
                };

            // whenever a new location is selected from the SearchFlyout (this is NOT the Search charm) update the position accordingly
            SearchFlyout.LocationChanged += (s, e) =>
            {
                GotoLocation(e.Position);
                SearchFlyout.Hide();
            };

            // manage SearchFlyout visibility/interaction
            this.Tapped += (s, e) =>
            {
                if (SearchFlyout.IsOpen)
                {
                    SearchFlyout.Hide();
                    e.Handled = true;
                }
            };
            BottomAppBar.Opened += (s, e) => { SearchFlyout.Hide(); };
            SearchFlyout.Tapped += (s, e) => { e.Handled = true; };

            // allow type-to-search for Search charm
            SearchPane.GetForCurrentView().ShowOnKeyboardInput = true;

            // The BingMaps API allows use of a "session key" if the application leverages the Bing Maps control. By using the session
            // key instead of the API key, only one transaction is logged agains the key versus one transaction for every API call! This 
            // code sets the key asynchronously and stored it as a resource so it's available when the REST API's are invoked.
            TheMap.Loaded += async (s, e) =>
            {
                if (!Application.Current.Resources.ContainsKey("BingMapsSessionKey"))
                    Application.Current.Resources.Add("BingMapsSessionKey", await TheMap.GetSessionIdAsync());
            };
        }

        /// <summary>
        /// Navigates map centering on given location
        /// </summary>
        /// <param name="position">Latitude/longitude point of new location (if null, current location is detected via GPS)</param>
        /// <param name="showMessage">Whether to show message informing user that location tracking is not enabled on device.</param>
        async Task GotoLocation(LatLong position, Boolean showMessage = false)
        {
            Boolean currentLocationRequested = position == null;

            // a null location is the cue to use geopositioning
            if (currentLocationRequested)
            {
                try
                {
                    _geolocator = new Geolocator();

                    // register callback to reset (hide) the user's location, if location access is revoked while app is running
                    _geolocator.StatusChanged += (s, a) =>
                    {
                        this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                            new Windows.UI.Core.DispatchedHandler(() =>
                            {
                                if (a.Status == PositionStatus.Disabled)
                                    _locationMarker.Visibility = Visibility.Collapsed;
                            })
                        );
                    };

                    Geoposition currentPosition = await _geolocator.GetGeopositionAsync();
                    position = new LatLong(currentPosition.Coordinate.Latitude, currentPosition.Coordinate.Longitude);
                }
                catch (Exception)
                {
                    // edge case for initial appearance of map when location services are turned off
                    App.InitialMapResizeHasOccurred = true;                    
                    
                    if (showMessage)
                    { 
                        MessageDialog md =
                            new MessageDialog("This application is unable to determine your current location; however, you can continue to use the other features of the application.\n\nThis condition can occur if you have turned off Location services for the application or are operating in Airplane mode. To reinstate the use of Location services, modify the Location setting on the Permissions flyout of the Settings charm.");
                        md.ShowAsync();
                    }
                }
            }

            // don't assume a valid location at this point GPS/Wifi disabled may lead to a null location
            if (position != null)
            {            
                // register event handler to do work once the view has been reset
                TheMap.ViewChangeEnded += TheMap_ViewChangeEndedWithRefreshNeeded;

                // set pin for current location
                if (currentLocationRequested) TheMap.SetCurrentLocationPin(_locationMarker, position);

                // pan map to desired location with a default zoom level (when complete, ViewChangeEndedWithRefreshNeeded event will fire)
                _noRefreshRequiredViewChange = true;
                TheMap.SetView(new Location(position.Latitude, position.Longitude), (Double)App.Current.Resources["DefaultZoomLevel"]);
            }
        }

        /// <summary>
        /// Fires when map panning is complete, and the current bounds of the map can be accessed to apply the points of interest
        /// (note that using TargetBounds in lieu of the event handler led to different results for the same target location depending on
        /// the visibilty of the map at the time of invocation - i.e., a navigation initiated from the search results pages reported sligthly
        /// offset TargetBounds
        /// </summary>
        async void TheMap_ViewChangeEndedWithRefreshNeeded(object sender, ViewChangeEndedEventArgs e)
        {
            // refresh the left panel to reflect points of interest in current view
            await LeftPanel.Refresh(new BoundingBox(TheMap.TargetBounds.North, TheMap.TargetBounds.South, TheMap.TargetBounds.West, TheMap.TargetBounds.East));

            // unregister the handler
            TheMap.ViewChangeEnded -= TheMap_ViewChangeEndedWithRefreshNeeded;
        }

        /// <summary>
        /// Access state information for the page
        /// </summary>
        /// <param name="navigationParameter">Parameter passsed in Navigate request; here it's a lat/long pair</param>
        /// <param name="pageState">Saved page state</param>
        protected override void LoadState(Object navigationParameter, Dictionary<String, Object> pageState)
        {
            // Restore page state
            if ((pageState != null) && (pageState.ContainsKey("MainPageState")))
                _pageState = pageState["MainPageState"] as MainPageState;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // initialize orientation (used later when determine if map view requires update)
            _priorOrientation = ApplicationView.Value;

            // register data callback for sharing
            var dtm = DataTransferManager.GetForCurrentView();
            dtm.DataRequested += LeftPanel.GetSharedData;

            // Navigate to new destination
            if (e.NavigationMode == NavigationMode.New)
            {
                LatLong destination;
                LatLong.TryParse(e.Parameter as String, out destination);

                GotoLocation(destination, showMessage: !_firstRun);
            }
            else
            {
                // refresh the point-of-interest list given coordinates saved in page state.  Note that Refresh is async, but
                // we are not awaiting it to lessen risk that a resume operation will timeout
                if (_pageState.MapBox != null) LeftPanel.Refresh(_pageState.MapBox, _pageState.SelectedItemId);

                // reset map to last known view
                _retainRefreshRequiredViewChange = true;
                TheMap.SetView(new Location(_pageState.MapCenter.Latitude, _pageState.MapCenter.Longitude), _pageState.Zoom, MapAnimationDuration.None);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // unregister data callback for sharing
            var dtm = DataTransferManager.GetForCurrentView();
            dtm.DataRequested -= LeftPanel.GetSharedData;

            // stop the autorefresh of the cameras since we're navigating away from the page
            LeftPanel.StopRefreshing();
        }

        /// <summary>
        /// Save state of the page
        /// </summary>
        /// <param name="pageState">State of the page to be saved in case of app termination while suspended</param>
        protected override void SaveState(Dictionary<string, object> pageState)
        {
            _pageState.PendingRefresh = (Boolean)this.DefaultViewModel["PendingRefresh"];
            pageState["MainPageState"] = _pageState;
        }

        #region AppBar implementations
        private void FindButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            // open the search flyout
            BottomAppBar.IsOpen = false;
            SearchFlyout.Show();
        }

        private async void LocationButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            // pan map to the user's curent location
            BottomAppBar.IsOpen = false;
            await GotoLocation(null, true);
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await LeftPanel.Refresh(new BoundingBox(TheMap.TargetBounds.North, TheMap.TargetBounds.South,
                                    TheMap.TargetBounds.West, TheMap.TargetBounds.East));
        }

        private void Aerial_Click(object sender, RoutedEventArgs e)
        {
            TheMap.MapType = (((ToggleButton)sender).IsChecked ?? false) ? MapType.Aerial : MapType.Road;
        }

        private void Traffic_Click(object sender, RoutedEventArgs e)
        {
            TheMap.ShowTraffic = ((ToggleButton)sender).IsChecked ?? false;
        }
        #endregion
    }
}