using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NesXbox.Logic;
using Windows.Storage;

namespace NesXbox.Services
{
    /// <summary>App-wide singletons. Everything persistent lives under ApplicationData.Current.LocalFolder.</summary>
    static class AppServices
    {
        public static LocalRomLibrary Library { get; private set; }
        public static SafeHttpClient Http { get; private set; }
        public static HomebrewHubCatalogClient HomebrewHub { get; private set; }
        public static GitHubReleaseCatalogClient GitHub { get; private set; }
        public static RomDownloadService Downloads { get; private set; }
        public static GamepadService Gamepads { get; private set; }
        public static AppSettings Settings { get; } = new AppSettings();

        /// <summary>Suspend/resume hooks for the running game (battery save, pause).</summary>
        public static event Action Suspending;
        public static event Action Resumed;

        static bool initialized;

        public static async Task InitializeAsync()
        {
            if (initialized) return;
            initialized = true;
            string root = ApplicationData.Current.LocalFolder.Path;
            Library = new LocalRomLibrary(root);
            await Library.LoadAsync();
            Http = new SafeHttpClient();
            HomebrewHub = new HomebrewHubCatalogClient(Http, Path.Combine(root, "CatalogCache"));
            GitHub = new GitHubReleaseCatalogClient(Http);
            Downloads = new RomDownloadService(Http, Library);
            Gamepads = new GamepadService();
            if (Library.RecoveredFromCorruption) Log("library.json was damaged and has been rebuilt");
        }

        public static void RaiseSuspending() => Suspending?.Invoke();
        public static void RaiseResumed() => Resumed?.Invoke();

        public static async Task ImportActivatedFilesAsync(IReadOnlyList<IStorageItem> items)
        {
            foreach (var item in items)
            {
                if (!(item is StorageFile f) || !f.Name.EndsWith(".nes", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var props = await f.GetBasicPropertiesAsync();
                    using (var s = await f.OpenStreamForReadAsync())
                        await Library.ImportAsync(s, f.Name, new ImportOrigin { Channel = ImportChannel.Usb }, (long)props.Size);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Log("Activation import failed: " + e.Message); }
            }
        }

        public static void Log(string message) => Debug.WriteLine("[NesXbox] " + message);
    }

    /// <summary>Small typed wrapper over LocalSettings.</summary>
    sealed class AppSettings
    {
        static ApplicationDataContainer Values => ApplicationData.Current.LocalSettings;

        T Get<T>(string key, T fallback) => Values.Values.TryGetValue(key, out var v) && v is T t ? t : fallback;
        void Set<T>(string key, T value) => Values.Values[key] = value;

        public bool RecursiveUsbScan { get => Get(nameof(RecursiveUsbScan), true); set => Set(nameof(RecursiveUsbScan), value); }
        public bool ShowDebugOverlay { get => Get(nameof(ShowDebugOverlay), false); set => Set(nameof(ShowDebugOverlay), value); }
        public bool PixelAspect87 { get => Get(nameof(PixelAspect87), false); set => Set(nameof(PixelAspect87), value); }
        public int GalleryFilter { get => Get(nameof(GalleryFilter), 0); set => Set(nameof(GalleryFilter), value); }
        public int GallerySort { get => Get(nameof(GallerySort), 0); set => Set(nameof(GallerySort), value); }
        public string LastSelectedGameId { get => Get<string>(nameof(LastSelectedGameId), null); set => Set(nameof(LastSelectedGameId), value); }
    }
}
