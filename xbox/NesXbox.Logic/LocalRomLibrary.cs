using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NesXbox.Logic
{
    public enum ImportStatus
    {
        Added,
        AlreadyInLibrary,
        InvalidRom,
        UnsupportedMapper,
        TooLarge,
        Incomplete,          // fewer/more bytes than the source announced
        Cancelled,
        SourceDisconnected,  // USB pulled, connection dropped
        AccessDenied,
        StorageFull,
        Failed,
    }

    public sealed class ImportOrigin
    {
        public string Channel { get; set; } = ImportChannel.Usb;
        public string SourceUrl { get; set; }
        public string License { get; set; }
        public string Developer { get; set; }
        public string Title { get; set; }    // preferred over the file name when set
    }

    public sealed class ImportResult
    {
        public ImportStatus Status { get; }
        public GameEntry Game { get; }
        public string Message { get; }
        public bool Succeeded => Status == ImportStatus.Added || Status == ImportStatus.AlreadyInLibrary;

        public ImportResult(ImportStatus status, GameEntry game = null, string message = null)
        {
            Status = status;
            Game = game;
            Message = message ?? Describe(status);
        }

        public static string Describe(ImportStatus s)
        {
            switch (s)
            {
                case ImportStatus.Added: return "Imported";
                case ImportStatus.AlreadyInLibrary: return "Already in library";
                case ImportStatus.InvalidRom: return "Invalid ROM";
                case ImportStatus.UnsupportedMapper: return "Unsupported mapper";
                case ImportStatus.TooLarge: return "File too large";
                case ImportStatus.Incomplete: return "Transfer incomplete";
                case ImportStatus.Cancelled: return "Cancelled";
                case ImportStatus.SourceDisconnected: return "Drive disconnected";
                case ImportStatus.AccessDenied: return "Access denied";
                case ImportStatus.StorageFull: return "Not enough storage";
                default: return "Import failed";
            }
        }
    }

    /// <summary>
    /// Persistent game library rooted at ApplicationData.Current.LocalFolder (or a temp dir in tests).
    /// Layout: Games/&lt;sha256&gt;.nes, Covers/&lt;sha256&gt;.png|jpg, Saves/&lt;sha256&gt;.sav, Temp/, library.json.
    /// Every source (USB, LAN, catalog) goes through <see cref="ImportAsync"/>.
    /// </summary>
    public sealed class LocalRomLibrary
    {
        const string LibraryFile = "library.json";
        const string BackupFile = "library.bak";
        static readonly Regex Sha256Hex = new Regex("^[0-9a-f]{64}$");

        readonly string root;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        List<GameEntry> games = new List<GameEntry>();

        public LocalRomLibrary(string rootPath)
        {
            root = rootPath;
            foreach (var d in new[] { "Games", "Covers", "Saves", "States", "Temp" }) Directory.CreateDirectory(Path.Combine(root, d));
        }

        /// <summary>Raised (on a worker thread) after any change to the game list or an entry.</summary>
        public event EventHandler Changed;

        public IReadOnlyList<GameEntry> Games => games;
        public string RootPath => root;
        public bool RecoveredFromCorruption { get; private set; }

        public string GetRomPath(GameEntry g) => Path.Combine(root, "Games", g.Sha256 + ".nes");
        public string GetSavePath(GameEntry g) => Path.Combine(root, "Saves", g.Sha256 + ".sav");
        public string GetStatePath(GameEntry g) => Path.Combine(root, "States", g.Sha256 + ".state");
        public string GetCoverPath(GameEntry g) => string.IsNullOrEmpty(g.CoverPath) ? null : Path.Combine(root, g.CoverPath);
        public GameEntry Find(string id) => games.FirstOrDefault(g => g.Id == id);

        // ------------------------------------------------------------------ load / repair

        public async Task LoadAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var f in Directory.GetFiles(Path.Combine(root, "Temp"))) TryDelete(f);

                bool dirty = false;
                var doc = TryRead(Path.Combine(root, LibraryFile));
                if (doc == null && File.Exists(Path.Combine(root, LibraryFile)))
                {
                    // Keep the damaged file for diagnosis, then fall back to the last good copy.
                    RecoveredFromCorruption = true;
                    ReplaceFile(Path.Combine(root, LibraryFile), Path.Combine(root, "library.corrupt.json"));
                    dirty = true;
                }
                if (doc == null) doc = TryRead(Path.Combine(root, BackupFile));
                if (doc == null) doc = new LibraryDocument();

                var byId = new Dictionary<string, GameEntry>();
                foreach (var g in doc.Games ?? new List<GameEntry>())
                {
                    if (g == null || g.Sha256 == null || !Sha256Hex.IsMatch(g.Sha256) || byId.ContainsKey(g.Sha256)) { dirty = true; continue; }
                    // Never trust stored paths: they are always derived from the hash.
                    g.Id = "sha256:" + g.Sha256;
                    g.RomPath = "Games/" + g.Sha256 + ".nes";
                    if (g.CoverPath != null && !IsSafeCoverPath(g.CoverPath, g.Sha256)) { g.CoverPath = null; dirty = true; }
                    if (string.IsNullOrWhiteSpace(g.Title)) { g.Title = "Untitled game"; dirty = true; }
                    if (g.Source == null) g.Source = ImportChannel.Recovered;
                    if (g.ImportedAt == null) g.ImportedAt = GameEntry.FormatTime(DateTime.UtcNow);
                    g.Available = File.Exists(GetRomPath(g));
                    byId[g.Sha256] = g;
                }

                // Orphaned ROM files (e.g. metadata lost) are re-registered instead of silently ignored.
                foreach (var file in Directory.GetFiles(Path.Combine(root, "Games"), "*.nes"))
                {
                    var sha = Path.GetFileNameWithoutExtension(file);
                    if (!Sha256Hex.IsMatch(sha) || byId.ContainsKey(sha)) continue;
                    var header = ReadHeader(file, out long length);
                    if (InesHeader.Check(header, length, out var h) == RomCheck.InvalidRom || h == null) continue;
                    byId[sha] = NewEntry(sha, "Recovered game " + sha.Substring(0, 8), h, length,
                        new ImportOrigin { Channel = ImportChannel.Recovered });
                    dirty = true;
                }

                games = byId.Values.ToList();
                if (dirty) Save();
            }
            finally { gate.Release(); }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        static bool IsSafeCoverPath(string p, string sha) =>
            p == "Covers/" + sha + ".png" || p == "Covers/" + sha + ".jpg";

        LibraryDocument TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var s = File.OpenRead(path))
                {
                    var doc = (LibraryDocument)new DataContractJsonSerializer(typeof(LibraryDocument)).ReadObject(s);
                    return doc != null && doc.SchemaVersion >= 1 && doc.SchemaVersion <= LibraryDocument.CurrentSchema ? doc : null;
                }
            }
            catch (Exception e) when (e is IOException || e is System.Runtime.Serialization.SerializationException ||
                                      e is InvalidCastException || e is System.Xml.XmlException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        void Save()
        {
            string tmp = Path.Combine(root, "library.tmp");
            var doc = new LibraryDocument { Games = games.OrderBy(g => g.ImportedAt, StringComparer.Ordinal).ToList() };
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(LibraryDocument)).WriteObject(fs, doc);
                fs.Flush(true);
            }
            string target = Path.Combine(root, LibraryFile);
            if (File.Exists(target))
            {
                try { File.Replace(tmp, target, Path.Combine(root, BackupFile)); return; }
                catch (PlatformNotSupportedException) { }
                File.Copy(target, Path.Combine(root, BackupFile), true);
            }
            ReplaceFile(tmp, target);
        }

        static void ReplaceFile(string from, string to)
        {
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }

        static byte[] ReadHeader(string path, out long length)
        {
            var header = new byte[16];
            using (var s = File.OpenRead(path))
            {
                length = s.Length;
                int n = 0;
                while (n < 16) { int r = s.Read(header, n, 16 - n); if (r <= 0) break; n += r; }
            }
            return header;
        }

        static void TryDelete(string path) { try { if (path != null && File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        // ------------------------------------------------------------------ import pipeline

        /// <summary>
        /// candidate stream -> size cap -> temp file + SHA-256 -> iNES/mapper check -> atomic commit -> metadata.
        /// Never throws for expected failures; the source stream is only read.
        /// </summary>
        public async Task<ImportResult> ImportAsync(Stream source, string fileName, ImportOrigin origin,
            long? expectedLength = null, IProgress<long> progress = null, CancellationToken ct = default)
        {
            if (origin == null) origin = new ImportOrigin();
            if (expectedLength > InesHeader.MaxRomBytes) return new ImportResult(ImportStatus.TooLarge);
            string temp = Path.Combine(root, "Temp", Guid.NewGuid().ToString("N") + ".part");
            try
            {
                var header = new byte[16];
                long total = 0;
                string sha;
                using (var hasher = SHA256.Create())
                using (var outFile = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buf = new byte[65536];
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n;
                        try { n = await source.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false); }
                        catch (UnauthorizedAccessException e) { return Fail(temp, ImportStatus.AccessDenied, e.Message); }
                        catch (Exception e) when (!(e is OperationCanceledException))
                        {
                            // USB unplug, dropped HTTP body or socket: anything that breaks the source.
                            return Fail(temp, ImportStatus.SourceDisconnected, e.Message);
                        }
                        if (n <= 0) break;
                        if (total < 16) Array.Copy(buf, 0, header, (int)total, (int)Math.Min(16 - total, n));
                        total += n;
                        if (total > InesHeader.MaxRomBytes) return Fail(temp, ImportStatus.TooLarge);
                        hasher.TransformBlock(buf, 0, n, null, 0);
                        try { await outFile.WriteAsync(buf, 0, n, ct).ConfigureAwait(false); }
                        catch (IOException e) when (IsDiskFull(e)) { return Fail(temp, ImportStatus.StorageFull); }
                        progress?.Report(total);
                    }
                    hasher.TransformFinalBlock(buf, 0, 0);
                    sha = ToHex(hasher.Hash);
                    await outFile.FlushAsync(ct).ConfigureAwait(false);
                }
                if (expectedLength.HasValue && expectedLength.Value != total)
                    return Fail(temp, ImportStatus.Incomplete, $"Expected {expectedLength} bytes, got {total}");

                switch (InesHeader.Check(header, total, out var parsed))
                {
                    case RomCheck.InvalidRom: return Fail(temp, ImportStatus.InvalidRom);
                    case RomCheck.TooLarge: return Fail(temp, ImportStatus.TooLarge);
                    case RomCheck.UnsupportedMapper:
                        return Fail(temp, ImportStatus.UnsupportedMapper, $"Unsupported mapper {parsed.Mapper}");
                }

                ct.ThrowIfCancellationRequested();
                await gate.WaitAsync(ct).ConfigureAwait(false);
                GameEntry entry;
                bool added = false;
                try
                {
                    entry = games.FirstOrDefault(g => g.Sha256 == sha);
                    if (entry != null && entry.Available)
                    {
                        TryDelete(temp);
                        return new ImportResult(ImportStatus.AlreadyInLibrary, entry);
                    }
                    string final = Path.Combine(root, "Games", sha + ".nes");
                    bool createdFile = !File.Exists(final);
                    if (createdFile) File.Move(temp, final); else TryDelete(temp);
                    if (entry == null)
                    {
                        string title = SanitizeTitle(origin.Title) ?? TitleFromFileName(fileName);
                        entry = NewEntry(sha, title, parsed, total, origin);
                        games.Add(entry);
                        added = true;
                    }
                    entry.Available = true;
                    try { Save(); }
                    catch (IOException e)
                    {
                        // Roll back so metadata and files stay consistent.
                        if (added) games.Remove(entry);
                        if (createdFile) TryDelete(final);
                        return new ImportResult(IsDiskFull(e) ? ImportStatus.StorageFull : ImportStatus.Failed, null, e.Message);
                    }
                }
                finally { gate.Release(); }
                Changed?.Invoke(this, EventArgs.Empty);
                return new ImportResult(added ? ImportStatus.Added : ImportStatus.AlreadyInLibrary, entry);
            }
            catch (OperationCanceledException) { return Fail(temp, ImportStatus.Cancelled); }
            catch (UnauthorizedAccessException e) { return Fail(temp, ImportStatus.AccessDenied, e.Message); }
            catch (IOException e) { return Fail(temp, IsDiskFull(e) ? ImportStatus.StorageFull : ImportStatus.Failed, e.Message); }
            finally { TryDelete(temp); } // no-op after a successful commit (the file was moved)
        }

        static ImportResult Fail(string temp, ImportStatus s, string message = null)
        {
            TryDelete(temp);
            return new ImportResult(s, null, message == null ? null : ImportResult.Describe(s) + ": " + message);
        }

        static bool IsDiskFull(IOException e)
        {
            int code = e.HResult & 0xFFFF;
            return code == 0x70 || code == 0x27; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        }

        static GameEntry NewEntry(string sha, string title, InesHeader h, long size, ImportOrigin o) => new GameEntry
        {
            Id = "sha256:" + sha,
            Sha256 = sha,
            RomPath = "Games/" + sha + ".nes",
            Title = title,
            Mapper = h.Mapper,
            Battery = h.Battery,
            Size = size,
            Source = o.Channel,
            SourceUrl = o.SourceUrl,
            License = o.License,
            Developer = o.Developer,
            ImportedAt = GameEntry.FormatTime(DateTime.UtcNow),
            Available = true,
        };

        static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Display title from an untrusted file name: no paths, no control chars, bounded length.</summary>
        public static string TitleFromFileName(string fileName)
        {
            string name = fileName ?? "";
            int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (slash >= 0) name = name.Substring(slash + 1);
            if (name.EndsWith(".nes", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return SanitizeTitle(name.Replace('_', ' ')) ?? "Untitled game";
        }

        public static string SanitizeTitle(string s)
        {
            if (s == null) return null;
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsControl(c) || char.IsSurrogate(c) ? ' ' : c);
            string t = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            if (t.Length > 80) t = t.Substring(0, 80).Trim();
            return t.Length == 0 ? null : t;
        }

        // ------------------------------------------------------------------ mutations

        async Task MutateAsync(string id, Action<GameEntry> change)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var g = Find(id);
                if (g == null) return;
                change(g);
                Save();
            }
            finally { gate.Release(); }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task SetFavoriteAsync(string id, bool favorite) => MutateAsync(id, g => g.Favorite = favorite);
        public Task MarkPlayedAsync(string id) => MutateAsync(id, g => g.LastPlayedAt = GameEntry.FormatTime(DateTime.UtcNow));

        public Task RenameAsync(string id, string title)
        {
            string t = SanitizeTitle(title);
            return t == null ? Task.CompletedTask : MutateAsync(id, g => g.Title = t);
        }

        /// <summary>Stores cover art (already-downloaded PNG/JPEG bytes). Failures never affect the ROM.</summary>
        public async Task<bool> SetCoverAsync(string id, byte[] image)
        {
            string ext = ImageExtension(image);
            if (ext == null || image.Length > 4 * 1024 * 1024) return false;
            var g = Find(id);
            if (g == null) return false;
            string rel = "Covers/" + g.Sha256 + ext;
            string tmp = Path.Combine(root, "Temp", Guid.NewGuid().ToString("N") + ext);
            try
            {
                File.WriteAllBytes(tmp, image);
                ReplaceFile(tmp, Path.Combine(root, rel));
            }
            catch (IOException) { TryDelete(tmp); return false; }
            await MutateAsync(id, e => e.CoverPath = rel).ConfigureAwait(false);
            return true;
        }

        static string ImageExtension(byte[] b)
        {
            if (b == null || b.Length < 8) return null;
            if (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return ".png";
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
            return null;
        }

        public async Task DeleteAsync(string id)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var g = Find(id);
                if (g == null) return;
                games.Remove(g);
                Save();
                // Files go after metadata: a crash in between leaves an orphan that LoadAsync re-registers,
                // never a record pointing at nothing. Save files are kept for a later re-import.
                TryDelete(GetRomPath(g));
                TryDelete(GetCoverPath(g));
            }
            finally { gate.Release(); }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public byte[] ReadRom(GameEntry g) => File.ReadAllBytes(GetRomPath(g));

        public byte[] ReadSave(GameEntry g)
        {
            string p = GetSavePath(g);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }

        public void WriteSave(GameEntry g, byte[] data) => WriteAtomic(GetSavePath(g), data);

        /// <summary>One quick-save slot per game (States/&lt;sha256&gt;.state).</summary>
        public void WriteState(GameEntry g, byte[] data) => WriteAtomic(GetStatePath(g), data);

        public byte[] ReadState(GameEntry g)
        {
            string p = GetStatePath(g);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }

        public DateTime? StateSavedAtUtc(GameEntry g)
        {
            string p = GetStatePath(g);
            return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : (DateTime?)null;
        }

        void WriteAtomic(string path, byte[] data)
        {
            string tmp = Path.Combine(root, "Temp", Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(tmp, data);
            ReplaceFile(tmp, path);
        }
    }
}
