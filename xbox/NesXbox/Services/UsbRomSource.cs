using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NesXbox.Logic;
using Windows.Devices.Enumeration;
using Windows.Storage;

namespace NesXbox.Services
{
    /// <summary>
    /// USB flash drives through KnownFolders.RemovableDevices. Needs the removableStorage capability plus the
    /// .nes fileTypeAssociation in Package.appxmanifest; only .nes files are visible to the app.
    /// Read-only: nothing here writes to the drive. Behaviour on a real Xbox is UNVERIFIED (docs/STATUS.md).
    /// </summary>
    public sealed class UsbRomSource : IRomSource, IDisposable
    {
        readonly DeviceWatcher watcher;

        /// <summary>Raised (on a worker thread) when a storage device is plugged in or removed.</summary>
        public event Action DevicesChanged;

        public UsbRomSource()
        {
            watcher = DeviceInformation.CreateWatcher(DeviceClass.PortableStorageDevice);
            bool enumerated = false;
            watcher.EnumerationCompleted += (s, e) => enumerated = true;
            watcher.Added += (s, e) => { if (enumerated) DevicesChanged?.Invoke(); };
            watcher.Removed += (s, e) => DevicesChanged?.Invoke();
            try { watcher.Start(); }
            catch (Exception e) when (e is UnauthorizedAccessException || e is InvalidOperationException) { AppServices.Log("DeviceWatcher: " + e.Message); }
        }

        public Task<RomScanResult> ScanAsync(CancellationToken ct)
        {
            var options = new ScanOptions { Recursive = AppServices.Settings.RecursiveUsbScan };
            return new RomScanner(GetVolumesAsync, options).ScanAsync(ct);
        }

        static async Task<IReadOnlyList<IScanFolder>> GetVolumesAsync()
        {
            var volumes = await KnownFolders.RemovableDevices.GetFoldersAsync();
            return volumes.Select(v => (IScanFolder)new StorageScanFolder(v, v.DisplayName)).ToList();
        }

        public void Dispose()
        {
            try { if (watcher.Status == DeviceWatcherStatus.Started || watcher.Status == DeviceWatcherStatus.EnumerationCompleted) watcher.Stop(); }
            catch (InvalidOperationException) { }
        }

        sealed class StorageScanFolder : IScanFolder
        {
            readonly StorageFolder folder;
            public StorageScanFolder(StorageFolder folder, string display) { this.folder = folder; DisplayPath = display; }
            public string Name => folder.Name;
            public string DisplayPath { get; }

            public async Task<IReadOnlyList<IScanFile>> GetFilesAsync()
            {
                var files = await folder.GetFilesAsync(); // shallow: one level per call
                return files.Where(f => f.FileType.Equals(".nes", StringComparison.OrdinalIgnoreCase))
                            .Select(f => (IScanFile)new StorageScanFile(f, DisplayPath + "\\" + f.Name)).ToList();
            }

            public async Task<IReadOnlyList<IScanFolder>> GetFoldersAsync()
            {
                var subs = await folder.GetFoldersAsync();
                return subs.Select(s => (IScanFolder)new StorageScanFolder(s, DisplayPath + "\\" + s.Name)).ToList();
            }
        }

        sealed class StorageScanFile : IScanFile
        {
            readonly StorageFile file;
            public StorageScanFile(StorageFile file, string display) { this.file = file; DisplayPath = display; }
            public string Name => file.Name;
            public string DisplayPath { get; }

            public async Task<long> GetSizeAsync() => (long)(await file.GetBasicPropertiesAsync()).Size;

            // FileAccessMode.Read only: the drive is never modified.
            public async Task<Stream> OpenReadAsync() => await file.OpenStreamForReadAsync();
        }
    }
}
