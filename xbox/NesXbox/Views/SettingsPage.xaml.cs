using System;
using System.IO;
using NesXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace NesXbox.Views
{
    public sealed partial class SettingsPage : Page
    {
        bool loading;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            loading = true;
            RecursiveToggle.IsOn = AppServices.Settings.RecursiveUsbScan;
            AspectToggle.IsOn = AppServices.Settings.PixelAspect87;
            DebugToggle.IsOn = AppServices.Settings.ShowDebugOverlay;
            loading = false;
            StorageText.Text = $"{AppServices.Library.Games.Count} games stored in {AppServices.Library.RootPath}";
        }

        void Recursive_Toggled(object sender, RoutedEventArgs e) { if (!loading) AppServices.Settings.RecursiveUsbScan = RecursiveToggle.IsOn; }
        void Aspect_Toggled(object sender, RoutedEventArgs e) { if (!loading) AppServices.Settings.PixelAspect87 = AspectToggle.IsOn; }
        void Debug_Toggled(object sender, RoutedEventArgs e) { if (!loading) AppServices.Settings.ShowDebugOverlay = DebugToggle.IsOn; }

        void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(AppServices.Library.RootPath, "CatalogCache");
            try
            {
                if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
                StorageText.Text = "Catalog cache cleared. Installed games are not affected.";
            }
            catch (IOException ex) { StorageText.Text = "Could not clear cache: " + ex.Message; }
        }
    }
}
