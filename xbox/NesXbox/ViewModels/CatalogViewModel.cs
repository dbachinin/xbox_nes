using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NesXbox.Logic;
using NesXbox.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace NesXbox.ViewModels
{
    public sealed class CatalogItem : Observable
    {
        string state;
        double progress;

        public CatalogItem(CatalogEntry entry) { Entry = entry; }

        public CatalogEntry Entry { get; }
        public string Title => Entry.Title;
        public string Developer => string.IsNullOrEmpty(Entry.Developer) ? "Unknown developer" : Entry.Developer;
        public string Rights => Entry.RightsText;
        public string Source => Entry.Provider == ImportChannel.GitHub ? "GitHub release" : "Homebrew Hub";
        // Remote screenshots are only shown for entries whose license permits redistribution.
        public ImageSource Cover => Entry.CoverUrl == null ? null : new BitmapImage(Entry.CoverUrl) { DecodePixelWidth = 160 };
        public Visibility CoverVisibility => Entry.CoverUrl == null ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>"Download", "In library", "Downloading 42%", "Official page only", "Download failed"...</summary>
        public string State { get => state; set => Set(ref state, value); }
        public double Progress { get => progress; set => Set(ref progress, value); }
        public bool Busy { get; set; }
        public CancellationTokenSource Cancel { get; set; }
    }

    public sealed class CatalogViewModel : Observable
    {
        readonly CoreDispatcher dispatcher;
        readonly HashSet<string> seen = new HashSet<string>();
        int page, pageTotal = 1;
        bool loading, githubMode;
        string status = "", search = "";

        public ObservableCollection<CatalogItem> Items { get; } = new ObservableCollection<CatalogItem>();

        public CatalogViewModel(CoreDispatcher dispatcher) { this.dispatcher = dispatcher; }

        public string Status { get => status; private set => Set(ref status, value); }
        public string Search { get => search; set => Set(ref search, value ?? ""); }
        public bool CanLoadMore => !githubMode && page < pageTotal && !loading;
        public Visibility LoadMoreVisibility => CanLoadMore ? Visibility.Visible : Visibility.Collapsed;

        public int SourceIndex
        {
            get => githubMode ? 1 : 0;
            set { if (githubMode != (value == 1)) { githubMode = value == 1; Raise(); _ = ReloadAsync(); } }
        }

        public async Task ReloadAsync()
        {
            Items.Clear();
            seen.Clear();
            page = 0;
            pageTotal = 1;
            await LoadMoreAsync();
        }

        public async Task LoadMoreAsync()
        {
            if (loading) return;
            loading = true;
            Status = "Browse Homebrew: loading…";
            try
            {
                List<CatalogEntry> entries;
                if (githubMode)
                {
                    entries = await AppServices.GitHub.GetEntriesAsync(CancellationToken.None);
                    Status = "Curated GitHub releases (licenses verified by hand, see docs/ROM_SOURCES.md)";
                }
                else
                {
                    var result = await AppServices.HomebrewHub.GetPageAsync(page + 1, search, CancellationToken.None);
                    page = result.Page;
                    pageTotal = result.PageTotal;
                    entries = result.Entries;
                    Status = result.FromCache
                        ? "Offline: showing catalog cached " + result.FetchedAtUtc.ToLocalTime().ToString("g")
                        : "Homebrew Hub — page " + page + " of " + pageTotal;
                }
                foreach (var e in entries.Where(e => seen.Add(e.Provider + ":" + e.Slug)))
                {
                    var item = new CatalogItem(e);
                    item.State = StateFor(item);
                    Items.Add(item);
                }
                if (Items.Count == 0) Status = "No NES homebrew found for this search.";
            }
            catch (NetException e)
            {
                Status = e.Kind == NetErrorKind.Offline || e.Kind == NetErrorKind.Timeout
                    ? "Offline: the catalog is unavailable. Your installed games still work."
                    : "Catalog error: " + e.Message;
            }
            finally
            {
                loading = false;
                Raise(nameof(CanLoadMore));
                Raise(nameof(LoadMoreVisibility));
            }
        }

        static string StateFor(CatalogItem item)
        {
            var e = item.Entry;
            string page = e.PageUrl?.ToString();
            if (page != null && AppServices.Library.Games.Any(g => g.SourceUrl == page)) return "In library";
            return e.Downloadable ? "Download" : "Official page only";
        }

        /// <summary>Downloads into the shared import pipeline; progress is reported on the UI thread.</summary>
        public async Task<ImportResult> DownloadAsync(CatalogItem item)
        {
            if (item.Busy || !item.Entry.Downloadable) return null;
            item.Busy = true;
            item.Cancel = new CancellationTokenSource();
            item.State = "Downloading";
            var progress = new SyncProgress<DownloadProgress>(p =>
            {
                var _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    item.Progress = (p.Fraction ?? 0) * 100;
                    item.State = p.Fraction.HasValue ? $"Downloading {p.Fraction.Value:P0}" : $"Downloading {p.Bytes / 1024} KiB";
                });
            });
            try
            {
                var r = await AppServices.Downloads.DownloadAsync(item.Entry, progress, item.Cancel.Token);
                item.State = r.Succeeded ? "In library" : r.Status == ImportStatus.Cancelled ? "Download" : "Download failed: " + r.Message;
                return r;
            }
            finally
            {
                item.Busy = false;
                item.Progress = 0;
            }
        }
    }
}
