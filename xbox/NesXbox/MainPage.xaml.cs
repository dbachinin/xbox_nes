using System;
using NesXbox.Services;
using NesXbox.Views;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace NesXbox
{
    /// <summary>Shell: tab bar + content frame. The game page replaces the whole shell while playing.</summary>
    public sealed partial class MainPage : Page
    {
        static readonly Type[] Sections = { typeof(GalleryPage), typeof(CatalogPage), typeof(UsbImportPage), typeof(LanImportPage), typeof(SettingsPage) };
        static int lastSection;

        public MainPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            KeyDown += OnKeyDown;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            AppServices.Gamepads.ConnectionChanged += OnPadsChanged;
            UpdatePadStatus();
            if (Tabs.SelectedIndex < 0) Tabs.SelectedIndex = lastSection;
            if (e.NavigationMode == NavigationMode.Back && ContentFrame.Content is GalleryPage gallery) gallery.RestoreSelection();
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            AppServices.Gamepads.ConnectionChanged -= OnPadsChanged;
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            base.OnNavigatedFrom(e);
        }

        public void ShowSection(int index) => Tabs.SelectedIndex = index;

        void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int i = Tabs.SelectedIndex;
            if (i < 0) return;
            lastSection = i;
            if (ContentFrame.CurrentSourcePageType != Sections[i]) ContentFrame.Navigate(Sections[i]);
        }

        void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            int count = Sections.Length;
            if (e.Key == VirtualKey.GamepadRightShoulder) { Tabs.SelectedIndex = (Tabs.SelectedIndex + 1) % count; e.Handled = true; }
            else if (e.Key == VirtualKey.GamepadLeftShoulder) { Tabs.SelectedIndex = (Tabs.SelectedIndex + count - 1) % count; e.Handled = true; }
        }

        // B on a section page returns focus to the tab bar instead of leaving the app.
        void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (Frame.CurrentSourcePageType != typeof(MainPage)) return;
            if (FocusManager.GetFocusedElement() is DependencyObject d && IsInside(d, Tabs)) return; // let the system handle it
            (Tabs.ContainerFromIndex(Math.Max(0, Tabs.SelectedIndex)) as Control)?.Focus(FocusState.Programmatic);
            e.Handled = true;
        }

        static bool IsInside(DependencyObject d, DependencyObject ancestor)
        {
            for (; d != null; d = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
                if (d == ancestor) return true;
            return false;
        }

        async void OnPadsChanged() => await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, UpdatePadStatus);

        void UpdatePadStatus()
        {
            int n = AppServices.Gamepads.ConnectedCount;
            PadStatus.Text = n == 0 ? "No controller" : n == 1 ? "1 controller" : n + " controllers";
        }
    }
}
