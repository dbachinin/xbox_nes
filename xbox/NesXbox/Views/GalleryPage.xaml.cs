using System;
using System.Linq;
using NesXbox.Logic;
using NesXbox.Services;
using NesXbox.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace NesXbox.Views
{
    public sealed partial class GalleryPage : Page
    {
        public GalleryViewModel ViewModel { get; }

        public GalleryPage()
        {
            ViewModel = new GalleryViewModel(Dispatcher);
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Attach();
            RestoreSelection();
        }

        /// <summary>Keeps the previously selected card selected (and focused) after returning from gameplay.</summary>
        public void RestoreSelection()
        {
            ViewModel.Refresh();
            var id = AppServices.Settings.LastSelectedGameId;
            var card = ViewModel.Cards.FirstOrDefault(c => c.Id == id) ?? ViewModel.Cards.FirstOrDefault();
            if (card == null) return;
            GamesGrid.SelectedItem = card;
            GamesGrid.ScrollIntoView(card);
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
                (GamesGrid.ContainerFromItem(card) as Control)?.Focus(FocusState.Programmatic));
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.Detach();
            base.OnNavigatedFrom(e);
        }

        async void Grid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is GameCard card)) return;
            AppServices.Settings.LastSelectedGameId = card.Id;
            var g = card.Entry;

            var favorite = new CheckBox { Content = "Favorite", IsChecked = g.Favorite, FontSize = 18 };
            var info = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 16,
                Text = $"Status: {GalleryQuery.StatusOf(g)}\nMapper: {g.Mapper}   Size: {g.Size / 1024} KiB\n" +
                       $"Source: {g.Source}{(g.SourceUrl != null ? "\n" + g.SourceUrl : "")}\n" +
                       $"Developer: {g.Developer ?? "unknown"}\nLicense: {g.License ?? "not recorded"}\n" +
                       $"Imported: {g.ImportedAtUtc.ToLocalTime():g}\nSHA-256: {g.Sha256.Substring(0, 16)}…",
            };
            var renameButton = new Button { Content = "Rename", FontSize = 18, Margin = new Thickness(0, 12, 0, 0) };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(favorite);
            panel.Children.Add(info);
            panel.Children.Add(renameButton);

            var dialog = new ContentDialog
            {
                Title = g.Title,
                Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
                PrimaryButtonText = g.Playable ? "Play" : "",
                SecondaryButtonText = "Delete",
                CloseButtonText = "Back",
                DefaultButton = g.Playable ? ContentDialogButton.Primary : ContentDialogButton.Close,
            };
            string action = null;
            renameButton.Click += (s, a) => { action = "rename"; dialog.Hide(); };
            var result = await dialog.ShowAsync();

            if (favorite.IsChecked != g.Favorite) await AppServices.Library.SetFavoriteAsync(g.Id, favorite.IsChecked == true);
            if (action == "rename") await RenameAsync(g);
            else if (result == ContentDialogResult.Primary) ((Frame)Window.Current.Content).Navigate(typeof(GamePage), g.Id); // full screen, no tab bar
            else if (result == ContentDialogResult.Secondary) await ConfirmDeleteAsync(g);
        }

        async System.Threading.Tasks.Task RenameAsync(GameEntry g)
        {
            var box = new TextBox { Text = g.Title, MaxLength = 80, FontSize = 18 };
            var dialog = new ContentDialog { Title = "Rename", Content = box, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await AppServices.Library.RenameAsync(g.Id, box.Text);
        }

        async System.Threading.Tasks.Task ConfirmDeleteAsync(GameEntry g)
        {
            var dialog = new ContentDialog
            {
                Title = "Delete " + g.Title + "?",
                Content = "The ROM is removed from the app's storage. Your USB drive is not touched; save data is kept.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await AppServices.Library.DeleteAsync(g.Id);
        }
    }
}
