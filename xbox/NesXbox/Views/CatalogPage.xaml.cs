using System;
using NesXbox.Logic;
using NesXbox.ViewModels;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace NesXbox.Views
{
    /// <summary>Opt-in online catalog. Separate from My Games: refreshing it never touches installed games.</summary>
    public sealed partial class CatalogPage : Page
    {
        public CatalogViewModel ViewModel { get; }

        public CatalogPage()
        {
            ViewModel = new CatalogViewModel(Dispatcher);
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (ViewModel.Items.Count == 0) await ViewModel.ReloadAsync();
        }

        async void Search_Click(object sender, RoutedEventArgs e) => await ViewModel.ReloadAsync();
        async void LoadMore_Click(object sender, RoutedEventArgs e) => await ViewModel.LoadMoreAsync();

        async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.GamepadMenu) { e.Handled = true; await ViewModel.ReloadAsync(); }
        }

        async void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is CatalogItem item)) return;
            var c = item.Entry;
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 16,
                IsTextSelectionEnabled = true,
                Text = (c.Description != null ? c.Description + "\n\n" : "") +
                       $"Developer: {c.Developer ?? "unknown"}\nType: {c.TypeTag}\n{c.RightsText}\n" +
                       (c.PageUrl != null ? "Official page: " + c.PageUrl + "\n" : "") +
                       (c.Downloadable ? "" : "\nTo play: download the .nes from the official page on a phone or PC, then use Import from USB or LAN Import."),
            };
            var dialog = new ContentDialog
            {
                Title = c.Title,
                Content = new ScrollViewer { Content = text, MaxHeight = 420 },
                CloseButtonText = "Back",
            };
            if (item.Busy) { dialog.PrimaryButtonText = "Cancel download"; }
            else if (c.Downloadable && item.State != "In library") { dialog.PrimaryButtonText = "Download"; dialog.DefaultButton = ContentDialogButton.Primary; }

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (item.Busy) { item.Cancel?.Cancel(); return; }
            var r = await ViewModel.DownloadAsync(item);
            if (r != null && r.Status == ImportStatus.UnsupportedMapper)
                item.State = "Unsupported mapper: not added";
        }
    }
}
