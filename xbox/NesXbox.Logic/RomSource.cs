using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NesXbox.Logic
{
    public enum RomSourceError
    {
        None,
        NoUsbDrive,
        AccessDenied,
        DriveDisconnected,
        Failed,
    }

    /// <summary>Minimal folder/file view so USB (StorageFolder), desktop dirs and test fakes share one scanner.</summary>
    public interface IScanFolder
    {
        string Name { get; }
        string DisplayPath { get; }
        Task<IReadOnlyList<IScanFile>> GetFilesAsync();
        Task<IReadOnlyList<IScanFolder>> GetFoldersAsync();
    }

    public interface IScanFile
    {
        string Name { get; }
        string DisplayPath { get; }
        Task<long> GetSizeAsync();
        Task<Stream> OpenReadAsync();
    }

    public sealed class RomCandidate
    {
        public string FileName { get; set; }
        public string Location { get; set; }
        public long Size { get; set; }
        public RomCheck Check { get; set; }
        public int Mapper { get; set; }
        public IScanFile File { get; set; }

        public bool Importable => Check == RomCheck.Ok;
        public string Title => LocalRomLibrary.TitleFromFileName(FileName);

        public string StatusText
        {
            get
            {
                switch (Check)
                {
                    case RomCheck.Ok: return "Ready (mapper " + Mapper + ")";
                    case RomCheck.UnsupportedMapper: return "Unsupported mapper " + Mapper;
                    case RomCheck.TooLarge: return "File too large";
                    default: return "Invalid ROM";
                }
            }
        }
    }

    public sealed class RomScanResult
    {
        public List<string> Volumes { get; } = new List<string>();
        public List<RomCandidate> Candidates { get; } = new List<RomCandidate>();
        public RomSourceError Error { get; set; }
        public string Message { get; set; }
        public bool Truncated { get; set; }   // file-count limit reached

        public static string Describe(RomSourceError e)
        {
            switch (e)
            {
                case RomSourceError.NoUsbDrive: return "No USB drive found";
                case RomSourceError.AccessDenied: return "Cannot access USB (access denied)";
                case RomSourceError.DriveDisconnected: return "Drive disconnected";
                case RomSourceError.Failed: return "Cannot access USB";
                default: return null;
            }
        }
    }

    public interface IRomSource
    {
        Task<RomScanResult> ScanAsync(CancellationToken ct);
    }

    public sealed class ScanOptions
    {
        public bool Recursive { get; set; } = true;
        public int MaxDepth { get; set; } = 3;          // root = 0
        public int MaxFiles { get; set; } = 2000;       // .nes files listed per scan
    }

    /// <summary>Scans volumes for .nes files: root first, then /NES, then (optionally) other folders.</summary>
    public sealed class RomScanner : IRomSource
    {
        readonly Func<Task<IReadOnlyList<IScanFolder>>> getVolumes;
        readonly ScanOptions options;

        public RomScanner(Func<Task<IReadOnlyList<IScanFolder>>> getVolumes, ScanOptions options = null)
        {
            this.getVolumes = getVolumes;
            this.options = options ?? new ScanOptions();
        }

        public async Task<RomScanResult> ScanAsync(CancellationToken ct)
        {
            var result = new RomScanResult();
            IReadOnlyList<IScanFolder> volumes;
            try { volumes = await getVolumes().ConfigureAwait(false); }
            catch (UnauthorizedAccessException e) { return Error(result, RomSourceError.AccessDenied, e); }
            catch (Exception e) when (e is IOException || e is InvalidOperationException) { return Error(result, RomSourceError.Failed, e); }
            if (volumes == null || volumes.Count == 0) return Error(result, RomSourceError.NoUsbDrive, null);

            foreach (var v in volumes)
            {
                ct.ThrowIfCancellationRequested();
                result.Volumes.Add(v.DisplayPath);
                try { await WalkAsync(v, 0, result, ct).ConfigureAwait(false); }
                catch (UnauthorizedAccessException e) { Error(result, RomSourceError.AccessDenied, e); }
                catch (Exception e) when (e is IOException || e is FileNotFoundException) { Error(result, RomSourceError.DriveDisconnected, e); }
                if (result.Truncated) break;
            }
            return result;
        }

        static RomScanResult Error(RomScanResult r, RomSourceError e, Exception ex)
        {
            r.Error = e;
            r.Message = RomScanResult.Describe(e) + (ex != null ? ": " + ex.Message : "");
            return r;
        }

        async Task WalkAsync(IScanFolder folder, int depth, RomScanResult result, CancellationToken ct)
        {
            foreach (var f in await folder.GetFilesAsync().ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (!f.Name.EndsWith(".nes", StringComparison.OrdinalIgnoreCase)) continue;
                if (result.Candidates.Count >= options.MaxFiles) { result.Truncated = true; return; }
                result.Candidates.Add(await ProbeAsync(f).ConfigureAwait(false));
            }
            bool descend = depth == 0 || (options.Recursive && depth < options.MaxDepth);
            if (!descend) return;
            var subs = (await folder.GetFoldersAsync().ConfigureAwait(false))
                // "NES" first; system folders are skipped.
                .OrderBy(s => string.Equals(s.Name, "NES", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Where(s => !s.Name.StartsWith(".") && !string.Equals(s.Name, "System Volume Information", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var s in subs)
            {
                if (depth == 0 && !options.Recursive && !string.Equals(s.Name, "NES", StringComparison.OrdinalIgnoreCase)) continue;
                await WalkAsync(s, depth + 1, result, ct).ConfigureAwait(false);
                if (result.Truncated) return;
            }
        }

        static async Task<RomCandidate> ProbeAsync(IScanFile f)
        {
            long size = await f.GetSizeAsync().ConfigureAwait(false);
            var c = new RomCandidate { FileName = f.Name, Location = f.DisplayPath, Size = size, File = f };
            if (size > InesHeader.MaxRomBytes) { c.Check = RomCheck.TooLarge; return c; }
            var header = new byte[16];
            using (var s = await f.OpenReadAsync().ConfigureAwait(false))
            {
                int n = 0;
                while (n < 16) { int r = await s.ReadAsync(header, n, 16 - n).ConfigureAwait(false); if (r <= 0) break; n += r; }
            }
            c.Check = InesHeader.Check(header, size, out var h);
            c.Mapper = h?.Mapper ?? -1;
            return c;
        }
    }

    public sealed class BatchProgress
    {
        public int Index { get; set; }       // 1-based
        public int Count { get; set; }
        public string FileName { get; set; }
        public long Bytes { get; set; }
        public string Text => $"Importing {Index}/{Count}";
    }

    public static class BatchImporter
    {
        /// <summary>Imports candidates one by one into the library. Source files are only opened for reading.</summary>
        public static async Task<List<(RomCandidate Candidate, ImportResult Result)>> ImportAsync(
            LocalRomLibrary library, IReadOnlyList<RomCandidate> items, string channel,
            IProgress<BatchProgress> progress = null, CancellationToken ct = default)
        {
            var results = new List<(RomCandidate, ImportResult)>();
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                if (ct.IsCancellationRequested) { results.Add((c, new ImportResult(ImportStatus.Cancelled))); continue; }
                var p = new BatchProgress { Index = i + 1, Count = items.Count, FileName = c.FileName };
                progress?.Report(p);
                ImportResult r;
                try
                {
                    using (var s = await c.File.OpenReadAsync().ConfigureAwait(false))
                    {
                        var byteProgress = progress == null ? null : new SyncProgress<long>(b =>
                            progress.Report(new BatchProgress { Index = p.Index, Count = p.Count, FileName = p.FileName, Bytes = b }));
                        r = await library.ImportAsync(s, c.FileName, new ImportOrigin { Channel = channel }, c.Size, byteProgress, ct)
                            .ConfigureAwait(false);
                    }
                }
                catch (UnauthorizedAccessException e) { r = new ImportResult(ImportStatus.AccessDenied, null, "Access denied: " + e.Message); }
                catch (Exception e) when (e is IOException || e is FileNotFoundException)
                {
                    r = new ImportResult(ImportStatus.SourceDisconnected, null, "Drive disconnected: " + e.Message);
                }
                results.Add((c, r));
            }
            return results;
        }
    }

    /// <summary>IProgress that invokes inline (Progress&lt;T&gt; posts to a captured context).</summary>
    public sealed class SyncProgress<T> : IProgress<T>
    {
        readonly Action<T> action;
        public SyncProgress(Action<T> action) { this.action = action; }
        public void Report(T value) => action(value);
    }

    /// <summary>System.IO-backed folder (desktop runs and tests).</summary>
    public sealed class DirectoryScanFolder : IScanFolder
    {
        readonly DirectoryInfo dir;
        public DirectoryScanFolder(string path) { dir = new DirectoryInfo(path); }
        public string Name => dir.Name;
        public string DisplayPath => dir.FullName;

        public Task<IReadOnlyList<IScanFile>> GetFilesAsync() =>
            Task.FromResult<IReadOnlyList<IScanFile>>(dir.GetFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => (IScanFile)new DirectoryScanFile(f)).ToList());

        public Task<IReadOnlyList<IScanFolder>> GetFoldersAsync() =>
            Task.FromResult<IReadOnlyList<IScanFolder>>(dir.GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => (IScanFolder)new DirectoryScanFolder(d.FullName)).ToList());

        sealed class DirectoryScanFile : IScanFile
        {
            readonly FileInfo file;
            public DirectoryScanFile(FileInfo f) { file = f; }
            public string Name => file.Name;
            public string DisplayPath => file.FullName;
            public Task<long> GetSizeAsync() => Task.FromResult(file.Length);
            public Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(file.OpenRead());
        }
    }
}
