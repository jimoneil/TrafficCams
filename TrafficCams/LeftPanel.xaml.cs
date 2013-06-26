using APIMASH.Mapping;
using APIMASH_TomTom;
using Bing.Maps;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using TrafficCams.Common;
using TrafficCams.Mapping;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

//
// LICENSE: http://aka.ms/LicenseTerms-SampleApps
//

namespace TrafficCams
{

    /// <summary>
    /// Event arguments providing the previous and currently selected item from the ListView in the panel
    /// </summary>
    public class ItemSelectedEventArgs : EventArgs
    {
        /// <summary>
        /// Item currently selected (possibly null)
        /// </summary>
        public IMappable NewItem { get; private set; }

        /// <summary>
        /// Item previously selected (possibly null)
        /// </summary>
        public IMappable OldItem { get; private set; }

        public ItemSelectedEventArgs(object newItem, object oldItem)
        {
            NewItem = newItem as IMappable;
            OldItem = oldItem as IMappable;
        }
    }

    /// <summary>
    /// Implementation of left-side panel displaying API-specific points of interest, with synchronization to
    /// Bing Maps control built-in.
    /// </summary>
    public sealed partial class LeftPanel : LayoutAwarePanel
    {
        DispatcherTimer SnapshotTimer = new DispatcherTimer();
        DispatcherTimer FrameTimer = new DispatcherTimer() { Interval = TimeSpan.FromMilliseconds(750) };

        /// <summary>
        /// Reference to map on the main page
        /// </summary>
        public Map Map { get; set; }

        #region MaxResults dependency property
        /// <summary>
        /// Maximum number of results that will appear in panel ListView (0 indicates no limit)
        /// </summary>
        public Int32 MaxResults
        {
            get { return (Int32)GetValue(MaxResultsProperty); }
            set { SetValue(MaxResultsProperty, value); }
        }
        public static readonly DependencyProperty MaxResultsProperty =
            DependencyProperty.Register("MaxResults", typeof(Int32), typeof(LeftPanel), new PropertyMetadata(0));
        #endregion

        #region Refreshed event handler
        /// <summary>
        /// Occurs when one results in the panel are refreshed allowing parent control to take appropriate actions
        /// </summary>
        public event EventHandler<EventArgs> Refreshed;
        private void OnRefreshed()
        {
            if (Refreshed != null) Refreshed(this, new EventArgs());
        }
        #endregion

        #region ItemSelected handler
        /// <summary>
        /// Occurs when item in the ListView of the panel is selected
        /// </summary>
        public event EventHandler<ItemSelectedEventArgs> ItemSelected;
        private void OnItemSelected(ItemSelectedEventArgs e)
        {
            if (ItemSelected != null) ItemSelected(this, e);
        }
        #endregion


        APIMASH_TomTom.TomTomApi _tomTomApi = new APIMASH_TomTom.TomTomApi();
        public LeftPanel()
        {
            this.InitializeComponent();

            // intialize generic elements of the view model
            this.DefaultViewModel["AppName"] = App.DisplayName;
            this.DefaultViewModel["NoResults"] = false;
            this.DefaultViewModel["ApiStatus"] = APIMASH.ApiResponseStatus.Default;
            this.DefaultViewModel["MoviePlaying"] = false;

            // event callback implementation for dismissing the error panel
            ErrorPanel.Dismissed += (s, e) => this.DefaultViewModel["ApiStatus"] = APIMASH.ApiResponseStatus.Default;

            // timers
            SnapshotTimer.Tick += SnapshotTimer_Tick;
            FrameTimer.Tick += FrameTimer_Tick;

            // set view model reference and register for collection changed event handling to sync with map
            this.DefaultViewModel["ApiViewModel"] = _tomTomApi.TomTomViewModel;
            _tomTomApi.TomTomViewModel.Results.CollectionChanged += Results_CollectionChanged;
        }

        public async void GetSharedData(DataTransferManager sender, DataRequestedEventArgs args)
        {
            try
            {
                var cam = _tomTomApi.TomTomViewModel.SelectedCamera;
                if ((cam != null) && (cam.TimeLapse.Count > 0))
                {

                    DataRequestDeferral deferral = args.Request.GetDeferral();

                    args.Request.Data.Properties.Title = String.Format("TomTom Camera: {0}", cam.CameraId);
                    args.Request.Data.Properties.Description = cam.Name;

                    // share a file
                    var file = await StorageFile.CreateStreamedFileAsync(
                        String.Format("{0}_{1}.jpg", cam.CameraId, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")),
                        async stream =>
                        {
                            await stream.WriteAsync(cam.LastImageBytes.AsBuffer());
                            await stream.FlushAsync();
                            stream.Dispose();
                        },
                        null);
                    args.Request.Data.SetStorageItems(new List<IStorageItem> { file });

                    // share as bitmap
                    InMemoryRandomAccessStream raStream = new InMemoryRandomAccessStream();
                    await raStream.WriteAsync(cam.LastImageBytes.AsBuffer());
                    await raStream.FlushAsync();
                    args.Request.Data.SetBitmap(RandomAccessStreamReference.CreateFromStream(raStream));

                    deferral.Complete();
                }
                else
                {
                    args.Request.FailWithDisplayText("Select a camera to share its latest image.");
                }
            }
            catch (Exception ex)
            {
                args.Request.FailWithDisplayText(ex.Message);
            }
        }

        private Object _flipCollectionLock = new Object();
        async void SnapshotTimer_Tick(object sender, object e)
        {
            TomTomCameraViewModel selectedCamera = _tomTomApi.TomTomViewModel.SelectedCamera;
            if (selectedCamera != null)
            {
                Boolean onLastImage = false;

                // check to see if frame should automatically advance - don't do so if on a different from last for sake of user experience
                lock (_flipCollectionLock)
                {
                    onLastImage = (FlipViewCollectionSource.View != null) && (FlipViewCollectionSource.View.CurrentPosition == FlipViewCollectionSource.View.Count - 1);
                }

                await _tomTomApi.GetCameraImage(selectedCamera);

                // update to last frame of current timelapse (if appropriate)
                lock (_flipCollectionLock)
                {
                    if (onLastImage && !(bool)this.DefaultViewModel["MoviePlaying"])
                        if (FlipViewCollectionSource.View != null) FlipViewCollectionSource.View.MoveCurrentToLast();
                }
            }
        }

        /// <summary>
        /// Carries out application-specific handling of the item selected in the listview. The synchronization with
        /// the map display is already accomodated.
        /// </summary>
        /// <param name="item">Newly selected item that should be cast to a view model class for further processing</param>
        private async Task ProcessSelectedItem(object item)
        {
            // stop timer thread
            StopRefreshing();

            // set view model
            _tomTomApi.TomTomViewModel.SelectedCamera = item as TomTomCameraViewModel;

            if (item != null)
            {
                // set to last frame of current time lapse
                lock (_flipCollectionLock)
                {
                    if (FlipViewCollectionSource.View != null) FlipViewCollectionSource.View.MoveCurrentToLast();
                }

                // get the next image (may appear to do nothing if image has not changed)
                await _tomTomApi.GetCameraImage(_tomTomApi.TomTomViewModel.SelectedCamera);

                // update to last frame of current time lapse
                lock (_flipCollectionLock)
                {
                    if (FlipViewCollectionSource.View != null) FlipViewCollectionSource.View.MoveCurrentToLast();
                }

                // set up timer to refresh the current camera view
                SnapshotTimer.Interval = new TimeSpan(0, 0, 0, 0, (Int32)(_tomTomApi.TomTomViewModel.SelectedCamera.RefreshRate * 1000));
                SnapshotTimer.Start();
            }
        }

        /// <summary>
        /// Refreshes the list of items obtained from the API and populates the view model
        /// </summary>
        /// <param name="box">Bounding box of current map view</param>
        /// <param name="id">Id of IMappable item that should be selected</param>
        public async Task Refresh(BoundingBox box, String id = null)
        {
            // stop timers
            StopRefreshing();

            // refresh cam list
            this.DefaultViewModel["ApiStatus"] = await _tomTomApi.GetCameras(box, this.MaxResults);
            this.DefaultViewModel["NoResults"] = _tomTomApi.TomTomViewModel.Results.Count == 0;

            // if there's an IMappable ID provided, select that item automatically
            if (id != null)
                MappableListView.SelectedItem = MappableListView.Items.Where((c) => (c as IMappable).Id == id).FirstOrDefault();

            // signal that panel has been refreshed
            OnRefreshed();
        }

        #region event handlers (API agnostic thus requiring no modification)

        // handle synchronization for new selection in the list with the map
        AsyncLock ListViewLock = new AsyncLock();
        private async void MappableListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            using (await ListViewLock.LockAsync())
            {
                // get newly selected and deseleced item
                object newItem = e.AddedItems.FirstOrDefault();
                object oldItem = e.RemovedItems.FirstOrDefault();

                // process new selection
                await ProcessSelectedItem(newItem);

                // HACK ALERT: explicitly setting visibility because a reset of list isn't triggering the rebinding so 
                // that the XAML converter for Visibility kicks in.  
                DetailsView.Visibility = newItem == null ? Visibility.Collapsed : Visibility.Visible;

                // attach handler to ensure selected item is in view after layout adjustments
                MappableListView.LayoutUpdated += MappableListView_LayoutUpdated;

                // notify event listeners that new item has been selected
                OnItemSelected(new ItemSelectedEventArgs(newItem, oldItem));
            }
        }

        // make sure selected item is visible whenever list updates
        void MappableListView_LayoutUpdated(object sender, object e)
        {
            MappableListView.ScrollIntoView(MappableListView.SelectedItem ?? MappableListView.Items.FirstOrDefault());
            MappableListView.LayoutUpdated -= MappableListView_LayoutUpdated;
        }

        // synchronize changes in the view model collection with the map push pins
        void Results_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // the synchronization requires a reference to a Bing.Maps object on the Main page
            if (Map == null)
                throw new System.NullReferenceException("An instance of Bing.Maps is required here, yet the Map property was found to be null.");

            // only additions and wholesale reset of the ObservableCollection are currently supported
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    foreach (var item in e.NewItems)
                    {
                        IMappable mapItem = (IMappable)item;

                        PointOfInterestPin poiPin = new PointOfInterestPin(mapItem);
                        poiPin.Selected += (s2, e2) =>
                        {
                            MappableListView.SelectedItem = MappableListView.Items.Where((c) => (c as IMappable).Id == e2.PointOfInterest.Id).FirstOrDefault();
                        };
                        poiPin.Tapped += (s3, e3) => { e3.Handled = true; };

                        Map.AddPointOfInterestPin(poiPin, mapItem.Position);
                    }
                    break;

                case NotifyCollectionChangedAction.Reset:
                    Map.ClearPointOfInterestPins();
                    break;

                // not implemented in this context
                // case NotifyCollectionChangesAction.Remove:
                // case NotifyCollectionChangedAction.Replace:
                // case NotifyCollectionChangedAction.Move:
            }
        }

        // invoke refresh when clicking on glyph next to title
        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (Map != null)
            {
                Refresh(new BoundingBox(Map.TargetBounds.North, Map.TargetBounds.South,
                    Map.TargetBounds.West, Map.TargetBounds.East));
            }
        }
        #endregion

        private void PlayMovie_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if ((Boolean)this.DefaultViewModel["MoviePlaying"])
                StopMovie();
            else
                StartMovie();
        }
        private void StartMovie()
        {
            lock (_flipCollectionLock)
            {
                if (FlipViewCollectionSource.View != null)
                {
                    this.DefaultViewModel["MoviePlaying"] = true;
                    if (FlipViewCollectionSource.View.CurrentPosition == FlipViewCollectionSource.View.Count - 1)
                        FlipViewCollectionSource.View.MoveCurrentToFirst();
                    FrameTimer.Start();
                }
            }
        }

        private void StopMovie()
        {
            this.DefaultViewModel["MoviePlaying"] = false;
            FrameTimer.Stop();
        }

        void FrameTimer_Tick(object sender, object e)
        {
            lock (_flipCollectionLock)
            {
                if (FlipViewCollectionSource.View != null)
                {
                    if (FlipViewCollectionSource.View.CurrentPosition == FlipViewCollectionSource.View.Count - 1)
                        StopMovie();
                    else
                        FlipViewCollectionSource.View.MoveCurrentToNext();
                }
            }
        }

        public void StopRefreshing()
        {
            StopMovie();
            SnapshotTimer.Stop();
        }

        private void MoreResults_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            FrameworkElement s = sender as FrameworkElement;

            // position popup right aligned with right edge of element that prompted its appearance
            Point p = s.TransformToVisual(MoreResultsPopup.Parent as UIElement).TransformPoint(new Point(0, 0));
            MoreResultsPopup.HorizontalOffset = p.X - MoreResultsPopup.ActualWidth + s.ActualWidth;
            MoreResultsPopup.VerticalOffset = p.Y + s.ActualHeight + 10;

            MoreResultsPopup.IsOpen = true;
        }

        private void CloseButton_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            MoreResultsPopup.IsOpen = false;
        }
    }
}
