using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NesXbox.Logic
{
    public sealed class CatalogEntry
    {
        public string Provider { get; set; }          // ImportChannel.HomebrewHub / GitHub
        public string Slug { get; set; }
        public string Title { get; set; }
        public string Developer { get; set; }
        public string License { get; set; }
        public string Description { get; set; }
        public string TypeTag { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public string RomFileName { get; set; }
        public Uri RomUrl { get; set; }
        public Uri CoverUrl { get; set; }             // only set when display is permitted
        public Uri PageUrl { get; set; }              // official page, shown as text, never fetched
        public bool Downloadable { get; set; }
        public string ReferenceReason { get; set; }   // why Download is not offered

        public string RightsText => Downloadable
            ? "License: " + License
            : (ReferenceReason ?? "Reference only");
    }

    public sealed class CatalogPage
    {
        public List<CatalogEntry> Entries { get; } = new List<CatalogEntry>();
        public int Page { get; set; }
        public int PageTotal { get; set; }
        public bool FromCache { get; set; }
        public DateTime FetchedAtUtc { get; set; }
    }

    /// <summary>Which declared licenses allow us to fetch and keep a copy of the ROM.</summary>
    public static class LicensePolicy
    {
        static readonly string[] Redistributable =
        {
            "CC0", "CC-BY", "GPL", "LGPL", "AGPL", "MIT", "BSD", "0BSD", "APACHE", "ZLIB", "UNLICENSE",
            "MPL", "ISC", "WTFPL", "PUBLIC DOMAIN", "PUBLIC-DOMAIN", "ARTISTIC", "BSL-1.0",
        };

        public static bool IsRedistributable(string license)
        {
            if (string.IsNullOrWhiteSpace(license)) return false;
            string l = license.Trim().ToUpperInvariant();
            if (l.Contains("PROPRIETARY") || l.Contains("ALL RIGHTS RESERVED")) return false;
            return Redistributable.Any(p => l.StartsWith(p, StringComparison.Ordinal));
        }
    }

    public static class SafePath
    {
        /// <summary>Validates a catalog-supplied relative path ("files/game.nes"): no traversal, no oddities.</summary>
        public static string[] Segments(string path, int maxSegments = 4)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 256 || path.IndexOf('\\') >= 0) return null;
            var parts = path.Split('/');
            if (parts.Length > maxSegments) return null;
            foreach (var p in parts)
                if (!IsSafeSegment(p)) return null;
            return parts;
        }

        public static bool IsSafeSegment(string s) =>
            !string.IsNullOrEmpty(s) && s.Length <= 128 && s != "." && s != ".." && s.IndexOf('/') < 0 &&
            s.IndexOf('\\') < 0 && s.All(c => !char.IsControl(c)) && s.Trim() == s;

        public static string Escape(string[] segments) => string.Join("/", segments.Select(Uri.EscapeDataString));
    }

    /// <summary>
    /// Homebrew Hub (https://hh.gbdev.io) NES catalog. Listing via the documented API; files and
    /// screenshots from the nesdev-org/homebrew-db dataset, because the documented
    /// /api/entry/{slug}/{file} route returned 404 when probed on 2026-10-20 (see docs/ROM_SOURCES.md).
    /// </summary>
    public sealed class HomebrewHubCatalogClient
    {
        public static readonly Uri ApiBase = new Uri("https://hh3.gbdev.io/api/");
        public static readonly Uri FilesBase = new Uri("https://raw.githubusercontent.com/nesdev-org/homebrew-db/master/entries/");
        const long MaxJsonBytes = 2 * 1024 * 1024;
        static readonly string[] AllowedTypes = { "game", "demo" };

        readonly SafeHttpClient http;
        readonly string cacheDir;

        public HomebrewHubCatalogClient(SafeHttpClient http, string cacheDir)
        {
            this.http = http;
            this.cacheDir = cacheDir;
            if (cacheDir != null) Directory.CreateDirectory(cacheDir);
        }

        /// <summary>Fetches one page; falls back to the cached copy when the network fails.</summary>
        public async Task<CatalogPage> GetPageAsync(int page, string title, CancellationToken ct)
        {
            if (page < 1) page = 1;
            string query = "search?platform=NES&page=" + page + (string.IsNullOrWhiteSpace(title) ? "" : "&title=" + Uri.EscapeDataString(title.Trim()));
            string cacheFile = cacheDir == null || !string.IsNullOrWhiteSpace(title) ? null : Path.Combine(cacheDir, "homebrewhub-page" + page + ".json");
            string body;
            bool fromCache = false;
            DateTime fetched = DateTime.UtcNow;
            try
            {
                body = await http.GetStringAsync(new Uri(ApiBase, query), MaxJsonBytes, ct).ConfigureAwait(false);
            }
            catch (NetException) when (cacheFile != null && File.Exists(cacheFile))
            {
                body = File.ReadAllText(cacheFile);
                fromCache = true;
                fetched = File.GetLastWriteTimeUtc(cacheFile);
            }
            var result = Parse(body);
            result.Page = page;
            result.FromCache = fromCache;
            result.FetchedAtUtc = fetched;
            if (!fromCache && cacheFile != null)
            {
                try { File.WriteAllText(cacheFile + ".tmp", body); if (File.Exists(cacheFile)) File.Delete(cacheFile); File.Move(cacheFile + ".tmp", cacheFile); }
                catch (IOException) { } // cache is best effort
            }
            return result;
        }

        public static CatalogPage Parse(string body)
        {
            object root;
            try { root = MiniJson.Parse(body); }
            catch (FormatException e) { throw new NetException(NetErrorKind.BadResponse, "Malformed catalog response", e); }
            var page = new CatalogPage { PageTotal = (int)(MiniJson.Num(root, "page_total") ?? 1) };
            var seen = new HashSet<string>();
            foreach (var e in MiniJson.Arr(root, "entries") ?? new List<object>())
            {
                var entry = FromJson(e);
                if (entry != null && seen.Add(entry.Slug)) page.Entries.Add(entry);
            }
            return page;
        }

        public static CatalogEntry FromJson(object e)
        {
            string slug = MiniJson.Str(e, "slug");
            if (slug == null || !SafePath.IsSafeSegment(slug)) return null;
            if (!string.Equals(MiniJson.Str(e, "platform"), "NES", StringComparison.OrdinalIgnoreCase)) return null;
            string type = (MiniJson.Str(e, "typetag") ?? "").ToLowerInvariant();
            if (Array.IndexOf(AllowedTypes, type) < 0) return null;

            // Prefer the default playable .nes file.
            string[] rom = null;
            string romName = null;
            foreach (var f in (MiniJson.Arr(e, "files") ?? new List<object>())
                     .OrderByDescending(f => (MiniJson.Bool(f, "default") ? 2 : 0) + (MiniJson.Bool(f, "playable") ? 1 : 0)))
            {
                string name = MiniJson.Str(f, "filename");
                var seg = SafePath.Segments(name);
                if (seg == null || !name.EndsWith(".nes", StringComparison.OrdinalIgnoreCase)) continue;
                rom = seg;
                romName = seg[seg.Length - 1];
                break;
            }
            if (rom == null) return null;

            string license = MiniJson.Str(e, "license");
            var entry = new CatalogEntry
            {
                Provider = ImportChannel.HomebrewHub,
                Slug = slug,
                Title = LocalRomLibrary.SanitizeTitle(MiniJson.Str(e, "title")) ?? slug,
                Developer = DeveloperText(e),
                License = license,
                Description = LocalRomLibrary.SanitizeTitle(Truncate(MiniJson.Str(e, "description"), 400)),
                TypeTag = type,
                RomFileName = romName,
                RomUrl = new Uri(FilesBase, Uri.EscapeDataString(slug) + "/" + SafePath.Escape(rom)),
                PageUrl = WebUrl(MiniJson.Str(e, "website")) ?? new Uri("https://hh.gbdev.io/game/" + Uri.EscapeDataString(slug)),
                Downloadable = LicensePolicy.IsRedistributable(license),
            };
            foreach (var t in MiniJson.Arr(e, "tags") ?? new List<object>())
                if (t is string s && entry.Tags.Count < 8) entry.Tags.Add(LocalRomLibrary.SanitizeTitle(s));
            if (!entry.Downloadable)
            {
                entry.RomUrl = null;
                entry.ReferenceReason = string.IsNullOrWhiteSpace(license)
                    ? "No license stated in the catalog: open the official page and import via USB/LAN"
                    : "License \"" + license + "\" does not clearly allow redistribution: use the official page";
            }
            else
            {
                // Screenshots are only cached for entries whose license permits redistribution.
                var shot = (MiniJson.Arr(e, "screenshots") ?? new List<object>()).OfType<string>().Select(s => SafePath.Segments(s))
                    .FirstOrDefault(s => s != null && (s.Last().EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                                       s.Last().EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)));
                if (shot != null) entry.CoverUrl = new Uri(FilesBase, Uri.EscapeDataString(slug) + "/" + SafePath.Escape(shot));
            }
            return entry;
        }

        static string Truncate(string s, int n) => s == null || s.Length <= n ? s : s.Substring(0, n) + "…";

        static Uri WebUrl(string s) =>
            Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") ? u : null;

        static string DeveloperText(object e)
        {
            if (!(e is Dictionary<string, object> d) || !d.TryGetValue("developer", out var v)) return null;
            IEnumerable<object> items = v is List<object> l ? l : new List<object> { v };
            var names = items.Select(x => x as string ?? MiniJson.Str(x, "name")).Where(n => !string.IsNullOrWhiteSpace(n));
            return LocalRomLibrary.SanitizeTitle(string.Join(", ", names));
        }
    }

    /// <summary>A GitHub repository whose release ROM a human verified as redistributable.</summary>
    public sealed class CuratedRepo
    {
        public string Owner { get; set; }
        public string Repo { get; set; }
        public string Title { get; set; }
        public string Developer { get; set; }
        public string License { get; set; }
        public string AssetName { get; set; }      // exact asset file name to download
        public string Description { get; set; }
    }

    /// <summary>GitHub Releases provider restricted to <see cref="Allowlist"/>; no search, no arbitrary repos.</summary>
    public sealed class GitHubReleaseCatalogClient
    {
        // Verified 2026-10-20 via the GitHub license API and each project's README.
        public static readonly IReadOnlyList<CuratedRepo> Allowlist = new[]
        {
            new CuratedRepo { Owner = "pinobatch", Repo = "thwaite-nes", Title = "Thwaite", Developer = "Damian Yerrick",
                License = "GPL-3.0-or-later", AssetName = "thwaite.nes", Description = "Missile-defense game for 1-2 players." },
            new CuratedRepo { Owner = "pinobatch", Repo = "croom-nes", Title = "Concentration Room", Developer = "Damian Yerrick",
                License = "GPL-3.0-or-later (exact iNES copies may be distributed without source)", AssetName = "croom.nes",
                Description = "Memory card-matching game." },
            new CuratedRepo { Owner = "pinobatch", Repo = "240p-test-mini", Title = "240p Test Suite", Developer = "Artemio Urbina, Damian Yerrick",
                License = "GPL-2.0-or-later", AssetName = "240pee.nes", Description = "Video test patterns for TVs and emulators." },
        };

        const long MaxJsonBytes = 1024 * 1024;
        readonly SafeHttpClient http;
        readonly IReadOnlyList<CuratedRepo> repos;

        public GitHubReleaseCatalogClient(SafeHttpClient http, IReadOnlyList<CuratedRepo> repos = null)
        {
            this.http = http;
            this.repos = repos ?? Allowlist;
        }

        public async Task<List<CatalogEntry>> GetEntriesAsync(CancellationToken ct)
        {
            var list = new List<CatalogEntry>();
            foreach (var r in repos)
            {
                var entry = new CatalogEntry
                {
                    Provider = ImportChannel.GitHub,
                    Slug = r.Owner + "/" + r.Repo,
                    Title = r.Title,
                    Developer = r.Developer,
                    License = r.License,
                    Description = r.Description,
                    TypeTag = "game",
                    RomFileName = r.AssetName,
                    PageUrl = new Uri("https://github.com/" + r.Owner + "/" + r.Repo),
                };
                try
                {
                    var body = await http.GetStringAsync(new Uri("https://api.github.com/repos/" + r.Owner + "/" + r.Repo + "/releases/latest"), MaxJsonBytes, ct)
                        .ConfigureAwait(false);
                    var release = MiniJson.Parse(body);
                    var asset = (MiniJson.Arr(release, "assets") ?? new List<object>()).FirstOrDefault(a => MiniJson.Str(a, "name") == r.AssetName);
                    if (asset != null && Uri.TryCreate(MiniJson.Str(asset, "browser_download_url"), UriKind.Absolute, out var url) && http.Policy.IsAllowed(url))
                    {
                        entry.RomUrl = url;
                        entry.Downloadable = true;
                        string tag = LocalRomLibrary.SanitizeTitle(MiniJson.Str(release, "tag_name"));
                        if (tag != null) entry.Title = r.Title + " " + tag;
                    }
                    else entry.ReferenceReason = "Release asset not found: open the project page";
                }
                catch (Exception e) when (e is NetException || e is FormatException)
                {
                    entry.ReferenceReason = "GitHub unavailable: " + e.Message;
                }
                list.Add(entry);
            }
            return list;
        }
    }

    public sealed class DownloadProgress
    {
        public long Bytes { get; set; }
        public long? Total { get; set; }
        public double? Fraction => Total > 0 ? (double)Bytes / Total.Value : (double?)null;
    }

    /// <summary>User-initiated catalog downloads through the shared import pipeline.</summary>
    public sealed class RomDownloadService
    {
        const long MaxCoverBytes = 2 * 1024 * 1024;
        readonly SafeHttpClient http;
        readonly LocalRomLibrary library;
        readonly SemaphoreSlim slots = new SemaphoreSlim(1, 1); // one download at a time

        public RomDownloadService(SafeHttpClient http, LocalRomLibrary library)
        {
            this.http = http;
            this.library = library;
        }

        public async Task<ImportResult> DownloadAsync(CatalogEntry e, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            if (!e.Downloadable || e.RomUrl == null)
                return new ImportResult(ImportStatus.Failed, null, "Not offered for direct download");
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using (var resp = await http.GetAsync(e.RomUrl, ct).ConfigureAwait(false))
                {
                    long? total = resp.Content.Headers.ContentLength;
                    if (total > InesHeader.MaxRomBytes) return new ImportResult(ImportStatus.TooLarge);
                    using (var body = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        var p = progress == null ? null : new SyncProgress<long>(b => progress.Report(new DownloadProgress { Bytes = b, Total = total }));
                        var origin = new ImportOrigin
                        {
                            Channel = e.Provider,
                            SourceUrl = (e.PageUrl ?? e.RomUrl).ToString(),
                            License = e.License,
                            Developer = e.Developer,
                            Title = e.Title,
                        };
                        var result = await library.ImportAsync(body, e.RomFileName, origin, total, p, ct).ConfigureAwait(false);
                        if (result.Status == ImportStatus.Added && e.CoverUrl != null) await TryFetchCoverAsync(result.Game, e.CoverUrl, ct).ConfigureAwait(false);
                        return result;
                    }
                }
            }
            catch (NetException ne)
            {
                return new ImportResult(ImportStatus.Failed, null, "Download failed: " + ne.Message);
            }
            catch (OperationCanceledException)
            {
                return new ImportResult(ImportStatus.Cancelled);
            }
            finally { slots.Release(); }
        }

        async Task TryFetchCoverAsync(GameEntry g, Uri url, CancellationToken ct)
        {
            try
            {
                var bytes = await http.GetBytesAsync(url, MaxCoverBytes, ct).ConfigureAwait(false);
                await library.SetCoverAsync(g.Id, bytes).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NetException || ex is IOException) { } // artwork never blocks an import
        }
    }
}
