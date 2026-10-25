using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NesXbox.Logic;
using NesXbox.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace NesXbox.ViewModels
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// <summary>One gallery card. Cover images are created lazily, so virtualised cards stay cheap.</summary>
    public sealed class GameCard : Observable
    {
        ImageSource cover;
        bool coverLoaded;

        public GameCard(GameEntry entry) { Entry = entry; }

        public GameEntry Entry { get; private set; }
        public string Id => Entry.Id;
        public string Title => Entry.Title;
        public string Status => GalleryQuery.StatusOf(Entry);
        public string Subtitle => Entry.LastPlayedAtUtc is DateTime t ? "Played " + t.ToLocalTime().ToString("d MMM yyyy") : (Entry.Developer ?? "");
        public Visibility FavoriteVisibility => Entry.Favorite ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlaceholderVisibility => Cover == null ? Visibility.Visible : Visibility.Collapsed;
        public Brush PlaceholderBrush => GameArtworkService.PlaceholderBrush(Entry);
        public string Initials => GameArtworkService.Initials(Entry.Title);
        public double StatusOpacity => Entry.Playable ? 0.7 : 1.0;

        public ImageSource Cover
        {
            get
            {
                if (!coverLoaded) { coverLoaded = true; cover = GameArtworkService.CoverFor(Entry); }
                return cover;
            }
        }

        public void Update(GameEntry e)
        {
            Entry = e;
            coverLoaded = false;
            Raise(string.Empty); // refresh every binding
        }
    }

    public sealed class GalleryViewModel : Observable
    {
        readonly CoreDispatcher dispatcher;
        GalleryFilter filter;
        GallerySort sort;
        string search = "";

        public ObservableCollection<GameCard> Cards { get; } = new ObservableCollection<GameCard>();

        public GalleryViewModel(CoreDispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            filter = (GalleryFilter)AppServices.Settings.GalleryFilter;
            sort = (GallerySort)AppServices.Settings.GallerySort;
        }

        public void Attach() { AppServices.Library.Changed += OnLibraryChanged; Refresh(); }
        public void Detach() => AppServices.Library.Changed -= OnLibraryChanged;

        public int FilterIndex
        {
            get => (int)filter;
            set { if (Set(ref filter, (GalleryFilter)value)) { AppServices.Settings.GalleryFilter = value; Refresh(); } }
        }

        public int SortIndex
        {
            get => (int)sort;
            set { if (Set(ref sort, (GallerySort)value)) { AppServices.Settings.GallerySort = value; Refresh(); } }
        }

        public string Search
        {
            get => search;
            set { if (Set(ref search, value ?? "")) Refresh(); }
        }

        public Visibility EmptyVisibility => Cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public string EmptyText => AppServices.Library.Games.Count == 0
            ? "No ROMs yet. Choose Import from USB, LAN import or Browse Homebrew to add games."
            : "No games match this filter.";

        public string CountText => AppServices.Library.Games.Count + " games";

        async void OnLibraryChanged(object sender, EventArgs e) =>
            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, Refresh);

        /// <summary>Rebuilds the visible list, reusing card objects by id so focus and selection survive.</summary>
        public void Refresh()
        {
            var wanted = GalleryQuery.Apply(AppServices.Library.Games, filter, sort, search);
            var existing = Cards.ToDictionary(c => c.Id);
            for (int i = 0; i < wanted.Count; i++)
            {
                if (existing.TryGetValue(wanted[i].Id, out var card))
                {
                    card.Update(wanted[i]);
                    int at = Cards.IndexOf(card);
                    if (at != i) Cards.Move(at, i);
                }
                else Cards.Insert(i, new GameCard(wanted[i]));
            }
            while (Cards.Count > wanted.Count) Cards.RemoveAt(Cards.Count - 1);
            Raise(nameof(EmptyVisibility));
            Raise(nameof(EmptyText));
            Raise(nameof(CountText));
        }
    }
}
