using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NesXbox.Logic;
using NesXbox.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace NesXbox.Views
{
    public sealed partial class UsbImportPage : Page
    {
        UsbRomSource source;
        CancellationTokenSource cts;
        bool importing;

        public UsbImportPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            source = new UsbRomSource();
            source.DevicesChanged += OnDevicesChanged;
            await ScanAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            cts?.Cancel();
            if (source != null) { source.DevicesChanged -= OnDevicesChanged; source.Dispose(); source = null; }
            base.OnNavigatedFrom(e);
        }

        async void OnDevicesChanged() =>
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => { if (!importing) await ScanAsync(); });

        async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

        /// <summary>Rescan is always allowed: it cancels a running scan and starts over.</summary>
        async Task ScanAsync()
        {
            if (importing || source == null) return;
            cts?.Cancel();
            var myCts = cts = new CancellationTokenSource();
            SetBusy(true, false, "Scanning USB…");
            Files.ItemsSource = null;
            var progress = new Progress<string>(where => { if (cts == myCts) StatusLine.Text = "Scanning USB: " + where; });
            try
            {
                var result = await source.ScanAsync(myCts.Token, progress);
                if (cts != myCts) return; // superseded by a newer scan
                Files.ItemsSource = result.Candidates;
                VolumesLine.Text = result.Volumes.Count == 0 ? "" : "Drives: " + string.Join(", ", result.Volumes);
                int ready = result.Candidates.Count(c => c.Importable);
                if (result.Error != RomSourceError.None && result.Candidates.Count == 0) StatusLine.Text = result.Message;
                else if (result.Candidates.Count == 0) StatusLine.Text = "No .nes files found on the USB drive.";
                else StatusLine.Text = $"{result.Candidates.Count} files found, {ready} ready to import" +
                                       (result.Truncated ? " (list truncated)" : "") +
                                       (result.Error != RomSourceError.None ? " — " + result.Message : "");
                foreach (var c in result.Candidates.Where(c => c.Importable)) Files.SelectedItems.Add(c);
                // Put the controller where the next action is.
                (ready > 0 ? ImportSelectedButton : RescanButton).Focus(FocusState.Keyboard);
            }
            catch (OperationCanceledException) { if (cts == myCts) StatusLine.Text = "Scan cancelled."; }
            catch (Exception ex) { StatusLine.Text = "Cannot access USB: " + ex.Message; }
            finally { if (cts == myCts) SetBusy(false, false, null); }
        }

        async void ImportSelected_Click(object sender, RoutedEventArgs e) =>
            await ImportAsync(Files.SelectedItems.OfType<RomCandidate>().ToList());

        async void ImportAll_Click(object sender, RoutedEventArgs e) =>
            await ImportAsync((Files.ItemsSource as IEnumerable<RomCandidate>)?.ToList() ?? new List<RomCandidate>());

        void Cancel_Click(object sender, RoutedEventArgs e) => cts?.Cancel();

        async Task ImportAsync(List<RomCandidate> items)
        {
            items = items.Where(c => c.Importable).ToList();
            if (importing || items.Count == 0) { StatusLine.Text = "Nothing to import: select files marked Ready."; return; }
            cts?.Cancel();
            SetBusy(true, true, $"Importing 1/{items.Count}");
            cts = new CancellationTokenSource();
            var progress = new Progress<BatchProgress>(p =>
            {
                StatusLine.Text = p.Text + ": " + p.FileName;
                Progress.Value = p.Count == 0 ? 0 : 100.0 * (p.Index - 1) / p.Count;
            });
            try
            {
                var results = await BatchImporter.ImportAsync(AppServices.Library, items, ImportChannel.Usb, progress, cts.Token);
                int added = results.Count(r => r.Result.Status == ImportStatus.Added);
                int dup = results.Count(r => r.Result.Status == ImportStatus.AlreadyInLibrary);
                var failed = results.Where(r => !r.Result.Succeeded).ToList();
                StatusLine.Text = $"Imported {added}, already in library {dup}, failed {failed.Count}." +
                                  (failed.Count > 0 ? " " + string.Join("; ", failed.Take(3).Select(f => f.Candidate.FileName + ": " + f.Result.Message)) : "");
            }
            finally { SetBusy(false, false, null); ImportSelectedButton.Focus(FocusState.Keyboard); }
        }

        void SetBusy(bool on, bool import, string status)
        {
            importing = on && import;
            if (status != null) StatusLine.Text = status;
            Progress.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Progress.IsIndeterminate = on && !import;
            CancelButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // Only an import locks the buttons; a scan can always be restarted with Rescan.
            ImportAllButton.IsEnabled = ImportSelectedButton.IsEnabled = !on;
            RescanButton.IsEnabled = !importing;
        }
    }
}
