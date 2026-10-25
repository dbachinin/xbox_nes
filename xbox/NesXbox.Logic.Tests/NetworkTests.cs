using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NesXbox.Logic;

namespace NesXbox.Logic.Tests
{
    static partial class Program
    {
        const string Raw = "https://raw.githubusercontent.com/nesdev-org/homebrew-db/master/entries/";

        static string Entry(string slug, string platform, string license, string file, string type = "game", string developer = "\"Dev\"") =>
            "{\"slug\":\"" + slug + "\",\"title\":\"T " + slug + "\",\"platform\":\"" + platform + "\",\"typetag\":\"" + type + "\"," +
            (license == null ? "" : "\"license\":\"" + license + "\",") +
            "\"developer\":" + developer + ",\"screenshots\":[\"shot.png\"],\"tags\":[\"Arcade\"]," +
            "\"files\":[{\"filename\":\"readme.txt\"},{\"default\":true,\"playable\":true,\"filename\":\"" + file + "\"}]}";

        // Scenario 13.3/1: 2 licensed NES games, 1 Game Boy, 1 NES with unknown rights (+ a paging duplicate).
        static readonly string SearchJson = "{\"results\":5,\"page_total\":2,\"page_current\":1,\"entries\":[" +
            Entry("alpha", "NES", "CC-BY-4.0", "files/alpha.nes") + "," +
            Entry("beta", "NES", "MIT", "beta.nes", "demo", "[\"A\",{\"name\":\"B\"}]") + "," +
            Entry("gbgame", "GB", "MIT", "gb.gb") + "," +
            Entry("mystery", "NES", null, "m.nes") + "," +
            Entry("alpha", "NES", "CC-BY-4.0", "files/alpha.nes") + "]}";

        static SafeHttpClient Http(MockHandler h) => new SafeHttpClient(h) { BaseDelay = TimeSpan.FromMilliseconds(1), Timeout = TimeSpan.FromSeconds(5) };

        static async Task CatalogFilters()
        {
            var h = new MockHandler { Respond = (r, ct) => Task.FromResult(MockHandler.Text(SearchJson)) };
            var client = new HomebrewHubCatalogClient(Http(h), null);
            var page = await client.GetPageAsync(1, null, default);
            Assert.Equal("https://hh3.gbdev.io/api/search?platform=NES&page=1", h.Requests.Single());
            Assert.Equal(3, page.Entries.Count);
            Assert.Equal(2, page.PageTotal);
            var alpha = page.Entries.Single(e => e.Slug == "alpha");
            Assert.True(alpha.Downloadable);
            Assert.Equal(Raw + "alpha/files/alpha.nes", alpha.RomUrl.AbsoluteUri);
            Assert.Equal(Raw + "alpha/shot.png", alpha.CoverUrl.AbsoluteUri);
            Assert.Equal("alpha.nes", alpha.RomFileName);
            Assert.Equal("A, B", page.Entries.Single(e => e.Slug == "beta").Developer);
            var mystery = page.Entries.Single(e => e.Slug == "mystery");
            Assert.True(!mystery.Downloadable && mystery.RomUrl == null && mystery.CoverUrl == null);
            Assert.True(mystery.RightsText.Contains("No license"));
            Assert.True(mystery.PageUrl != null);

            await client.GetPageAsync(2, "Alpha Game", default);
            Assert.Equal("https://hh3.gbdev.io/api/search?platform=NES&page=2&title=Alpha%20Game", h.Requests.Last());
        }

        static MockHandler CatalogServer(byte[] rom, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> romResponder = null)
        {
            return new MockHandler
            {
                Respond = (r, ct) =>
                {
                    string u = r.RequestUri.AbsoluteUri;
                    if (u.Contains("/api/search")) return Task.FromResult(MockHandler.Text(SearchJson));
                    if (u == Raw + "alpha/files/alpha.nes") return romResponder != null ? romResponder(r, ct) : Task.FromResult(MockHandler.Bytes(rom));
                    if (u == Raw + "alpha/shot.png") return Task.FromResult(MockHandler.Bytes(new byte[] { 0x89, 80, 78, 71, 13, 10, 26, 10, 1, 2 }, "image/png"));
                    return Task.FromResult(MockHandler.Status(HttpStatusCode.NotFound));
                },
            };
        }

        static async Task CatalogDownload()
        {
            using var t = new TempDir();
            var rom = Roms.Make(555);
            var http = Http(CatalogServer(rom));
            var lib = await Open(t.Path);
            var page = await new HomebrewHubCatalogClient(http, null).GetPageAsync(1, null, default);
            var alpha = page.Entries.Single(e => e.Slug == "alpha");
            var reports = new List<DownloadProgress>();
            var r = await new RomDownloadService(http, lib).DownloadAsync(alpha, new SyncProgress<DownloadProgress>(reports.Add), default);
            Assert.Equal(ImportStatus.Added, r.Status);
            Assert.True(reports.Count > 0 && reports.Last().Bytes == rom.Length && reports.Last().Fraction == 1.0);

            // Offline restart: no HTTP client at all, the game is still there with attribution and cover.
            lib = await Open(t.Path);
            var g = lib.Games.Single();
            Assert.Equal("T alpha", g.Title);
            Assert.Equal(ImportChannel.HomebrewHub, g.Source);
            Assert.Equal("CC-BY-4.0", g.License);
            Assert.Equal("Dev", g.Developer);
            Assert.True(g.SourceUrl.StartsWith("https://hh.gbdev.io/game/alpha"));
            Assert.True(File.Exists(lib.GetCoverPath(g)));
            Assert.True(g.Playable);

            // Reference-only entries never download.
            var mystery = page.Entries.Single(e => e.Slug == "mystery");
            Assert.Equal(ImportStatus.Failed, (await new RomDownloadService(http, lib).DownloadAsync(mystery, null, default)).Status);
        }

        static async Task CatalogCancelRetry()
        {
            using var t = new TempDir();
            var rom = Roms.Make(777);
            bool stall = true;
            var h = CatalogServer(rom, (r, ct) =>
            {
                if (!stall) return Task.FromResult(MockHandler.Bytes(rom));
                var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(rom.Take(4096).ToArray())) };
                resp.Content.Headers.ContentLength = rom.Length;
                return Task.FromResult(resp);
            });
            var http = Http(h);
            var lib = await Open(t.Path);
            var alpha = (await new HomebrewHubCatalogClient(http, null).GetPageAsync(1, null, default)).Entries.Single(e => e.Slug == "alpha");
            var svc = new RomDownloadService(http, lib);
            using (var cts = new CancellationTokenSource(300))
                Assert.Equal(ImportStatus.Cancelled, (await svc.DownloadAsync(alpha, null, cts.Token)).Status);
            Assert.Equal(0, lib.Games.Count);
            Assert.Equal(0, FileCount(t.Path, "Temp"));
            Assert.Equal(0, FileCount(t.Path, "Games"));

            stall = false;
            Assert.Equal(ImportStatus.Added, (await svc.DownloadAsync(alpha, null, default)).Status);
            // The same bytes from USB collapse into the catalog entry.
            Assert.Equal(ImportStatus.AlreadyInLibrary, (await Import(lib, rom, "from-usb.nes")).Status);
            Assert.Equal(1, lib.Games.Count);
        }

        static async Task CatalogErrors()
        {
            using var t = new TempDir();
            var lib = await Open(t.Path);
            var alpha = HomebrewHubCatalogClient.Parse(SearchJson).Entries.Single(e => e.Slug == "alpha");

            async Task<ImportResult> DownloadWith(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) =>
                await new RomDownloadService(Http(new MockHandler { Respond = f }), lib).DownloadAsync(alpha, null, default);

            // API down: 3 attempts then a recoverable error.
            int calls = 0;
            var down = new MockHandler { Respond = (r, ct) => { calls++; return Task.FromResult(MockHandler.Status(HttpStatusCode.ServiceUnavailable)); } };
            var e1 = await Assert.Throws<NetException>(() => new HomebrewHubCatalogClient(Http(down), null).GetPageAsync(1, null, default));
            Assert.Equal(NetErrorKind.ServerError, e1.Kind);
            Assert.Equal(3, calls);

            var bad = new MockHandler { Respond = (r, ct) => Task.FromResult(MockHandler.Text("{\"entries\":[{\"slug\":")) };
            Assert.Equal(NetErrorKind.BadResponse, (await Assert.Throws<NetException>(() => new HomebrewHubCatalogClient(Http(bad), null).GetPageAsync(1, null, default))).Kind);

            Assert.Equal(ImportStatus.InvalidRom, (await DownloadWith((r, c) => Task.FromResult(MockHandler.Text("<!DOCTYPE html><html>404</html>", "text/html")))).Status);
            var nf = await DownloadWith((r, c) => Task.FromResult(MockHandler.Status(HttpStatusCode.NotFound)));
            Assert.True(nf.Status == ImportStatus.Failed && nf.Message.Contains("Not found"));
            var off = await DownloadWith((r, c) => throw new HttpRequestException("no route to host"));
            Assert.True(off.Status == ImportStatus.Failed && off.Message.Contains("Network unavailable"));
            var rom = Roms.Make(9);
            var shortBody = rom.Take(10000).ToArray();
            var mismatch = await DownloadWith((r, c) =>
            {
                var resp = MockHandler.Bytes(shortBody);
                resp.Content.Headers.ContentLength = rom.Length;  // server lies about the length
                return Task.FromResult(resp);
            });
            Assert.True(mismatch.Status == ImportStatus.Incomplete || mismatch.Status == ImportStatus.SourceDisconnected, mismatch.Status.ToString());
            Assert.Equal(ImportStatus.UnsupportedMapper, (await DownloadWith((r, c) => Task.FromResult(MockHandler.Bytes(Roms.Make(1, mapper: 9))))).Status);
            Assert.Equal(ImportStatus.TooLarge, (await DownloadWith((r, c) =>
            {
                var resp = MockHandler.Bytes(new byte[16]);
                resp.Content.Headers.ContentLength = 100L * 1024 * 1024;
                return Task.FromResult(resp);
            })).Status);

            // Screenshot unavailable: the ROM still imports, just without a cover.
            var noShot = await DownloadWith((r, c) => Task.FromResult(r.RequestUri.AbsoluteUri.EndsWith(".nes")
                ? MockHandler.Bytes(rom) : MockHandler.Status(HttpStatusCode.InternalServerError)));
            Assert.Equal(ImportStatus.Added, noShot.Status);
            Assert.Equal(null, lib.Find(noShot.Game.Id).CoverPath);
            Assert.Equal(1, lib.Games.Count);
            Assert.Equal(0, FileCount(t.Path, "Temp"));
        }

        static async Task CatalogOfflineCache()
        {
            using var t = new TempDir();
            bool online = true;
            var h = new MockHandler
            {
                Respond = (r, ct) => online ? Task.FromResult(MockHandler.Text(SearchJson)) : throw new HttpRequestException("offline"),
            };
            var client = new HomebrewHubCatalogClient(Http(h), t.Sub("cache"));
            Assert.True(!(await client.GetPageAsync(1, null, default)).FromCache);
            online = false;
            var cached = await client.GetPageAsync(1, null, default);
            Assert.True(cached.FromCache);
            Assert.Equal(3, cached.Entries.Count);
            Assert.True(cached.FetchedAtUtc <= DateTime.UtcNow);
            var e = await Assert.Throws<NetException>(() => client.GetPageAsync(2, null, default));
            Assert.Equal(NetErrorKind.Offline, e.Kind);
        }

        static async Task CatalogRedirectPolicy()
        {
            var p = UrlPolicy.Default;
            Assert.True(p.IsAllowed(new Uri("https://raw.githubusercontent.com/x")));
            Assert.True(!p.IsAllowed(new Uri("http://raw.githubusercontent.com/x")));
            Assert.True(!p.IsAllowed(new Uri("https://evil.example/x")));
            Assert.True(!p.IsAllowed(new Uri("https://127.0.0.1/x")));
            Assert.True(!p.IsAllowed(new Uri("https://192.168.1.10/x")));
            Assert.True(!p.IsAllowed(new Uri("https://user:pw@raw.githubusercontent.com/x")));
            Assert.True(!p.IsAllowed(new Uri("https://raw.githubusercontent.com:8443/x")));
            Assert.True(!p.IsAllowed(new Uri("file:///etc/passwd")));

            foreach (var target in new[] { "http://raw.githubusercontent.com/a.nes", "https://10.0.0.5/a.nes", "file:///c:/x.nes", "https://localhost/a.nes" })
            {
                var h = new MockHandler { Respond = (r, ct) => Task.FromResult(MockHandler.Redirect(target)) };
                var e = await Assert.Throws<NetException>(() => Http(h).GetAsync(new Uri("https://github.com/a"), default));
                Assert.Equal(NetErrorKind.Blocked, e.Kind);
                Assert.Equal(1, h.Requests.Count);  // the blocked hop is never requested
            }
            // Allowed relative redirect chain is followed.
            var chain = new MockHandler
            {
                Respond = (r, ct) => Task.FromResult(r.RequestUri.AbsolutePath == "/start" ? MockHandler.Redirect("/final") : MockHandler.Text("ok")),
            };
            using (var resp = await Http(chain).GetAsync(new Uri("https://github.com/start"), default))
                Assert.Equal("ok", await resp.Content.ReadAsStringAsync());
            var loop = new MockHandler { Respond = (r, ct) => Task.FromResult(MockHandler.Redirect("https://github.com/again")) };
            Assert.Equal(NetErrorKind.Blocked, (await Assert.Throws<NetException>(() => Http(loop).GetAsync(new Uri("https://github.com/a"), default))).Kind);
        }

        static Task CatalogPathSafety()
        {
            Assert.True(SafePath.Segments("files/game.nes") != null);
            foreach (var bad in new[] { "../x.nes", "a/../../x.nes", "/abs.nes", "a//b.nes", "a\\b.nes", "a/b/c/d/e.nes", " x.nes", "a/\u0001.nes", "" })
                Assert.True(SafePath.Segments(bad) == null, bad);
            Assert.True(HomebrewHubCatalogClient.FromJson(MiniJson.Parse(Entry("..", "NES", "MIT", "a.nes"))) == null);
            Assert.True(HomebrewHubCatalogClient.FromJson(MiniJson.Parse(Entry("ok", "NES", "MIT", "../../etc/passwd.nes"))) == null);
            Assert.True(HomebrewHubCatalogClient.FromJson(MiniJson.Parse(Entry("ok", "NES", "MIT", "readme.txt"))) == null);
            // Real-world names containing '%' must be escaped, not decoded (see homebrew-db guild-quest screenshots).
            var e = HomebrewHubCatalogClient.FromJson(MiniJson.Parse(Entry("x", "NES", "MIT", "files/p%2F%2Fq.nes")));
            Assert.Equal(Raw + "x/files/p%252F%252Fq.nes", e.RomUrl.AbsoluteUri);
            Assert.True(LicensePolicy.IsRedistributable("CC-BY-NC-4.0") && LicensePolicy.IsRedistributable("GPL-3.0-or-later"));
            Assert.True(!LicensePolicy.IsRedistributable("Proprietary") && !LicensePolicy.IsRedistributable("") && !LicensePolicy.IsRedistributable("Freeware"));
            return Task.CompletedTask;
        }

        static async Task GitHubProvider()
        {
            var h = new MockHandler
            {
                Respond = (r, ct) =>
                {
                    string u = r.RequestUri.AbsoluteUri;
                    if (u.EndsWith("/thwaite-nes/releases/latest"))
                        return Task.FromResult(MockHandler.Text("{\"tag_name\":\"v0.04\",\"assets\":[{\"name\":\"src.zip\",\"browser_download_url\":\"https://github.com/x/src.zip\"}," +
                                                                "{\"name\":\"thwaite.nes\",\"browser_download_url\":\"https://github.com/pinobatch/thwaite-nes/releases/download/v0.04/thwaite.nes\"}]}"));
                    if (u.EndsWith("/croom-nes/releases/latest"))
                        return Task.FromResult(MockHandler.Text("{\"tag_name\":\"v1\",\"assets\":[{\"name\":\"croom.nes\",\"browser_download_url\":\"https://evil.example/croom.nes\"}]}"));
                    return Task.FromResult(MockHandler.Status(HttpStatusCode.NotFound));
                },
            };
            var entries = await new GitHubReleaseCatalogClient(Http(h)).GetEntriesAsync(default);
            Assert.Equal(3, entries.Count);
            var thwaite = entries[0];
            Assert.True(thwaite.Downloadable);
            Assert.Equal("Thwaite v0.04", thwaite.Title);
            Assert.Equal("https://github.com/pinobatch/thwaite-nes/releases/download/v0.04/thwaite.nes", thwaite.RomUrl.AbsoluteUri);
            Assert.True(!entries[1].Downloadable, "asset on a non-allowlisted host is refused");
            Assert.True(!entries[2].Downloadable && entries[2].ReferenceReason.Contains("GitHub unavailable"));
            Assert.True(h.Requests.All(u => u.StartsWith("https://api.github.com/repos/")));
        }

        static Task MiniJsonEdgeCases()
        {
            var v = MiniJson.Parse("{\"a\":[1,2.5,-3e2,true,false,null,\"x\\u00e9\\n\\\"\"],\"b\":{}}");
            var a = MiniJson.Arr(v, "a");
            Assert.Equal(7, a.Count);
            Assert.Equal(-300.0, (double)a[2]);
            Assert.Equal("xé\n\"", (string)a[6]);
            foreach (var bad in new[] { "", "{", "[1,]", "{\"a\" 1}", "\"open", "tru", "{} x", new string('[', 100) + new string(']', 100) })
            {
                bool threw = false;
                try { MiniJson.Parse(bad); } catch (FormatException) { threw = true; }
                Assert.True(threw, bad.Length > 20 ? "deep nesting" : bad);
            }
            Assert.Equal(null, MiniJson.Str(v, "missing"));
            Assert.Equal(null, MiniJson.Str("not a dict", "a"));
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------ LAN

        static async Task<(int Code, string Body)> Http11(int port, string request, byte[] body = null)
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
            var s = c.GetStream();
            var head = Encoding.ASCII.GetBytes(request);
            await s.WriteAsync(head, 0, head.Length);
            if (body != null) await s.WriteAsync(body, 0, body.Length);
            using var reader = new StreamReader(s);
            string all = await reader.ReadToEndAsync();
            int code = int.Parse(all.Split(' ')[1]);
            return (code, all.Substring(all.IndexOf("\r\n\r\n") + 4));
        }

        static async Task LanUpload()
        {
            using var t = new TempDir();
            var lib = await Open(t.Path);
            using var server = new LanUploadServer(lib, IPAddress.Loopback, 0);
            var imported = new List<ImportResult>();
            server.Imported += imported.Add;
            server.Start();
            int port = server.Port;
            Assert.Equal(6, server.Code.Length);

            var page = await Http11(port, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");
            Assert.True(page.Code == 200 && page.Body.Contains("<input id=files"));
            var rom = Roms.Make(31337);
            string Put(string code, long len) => $"PUT /upload?name=My%20Game.nes&code={code} HTTP/1.1\r\nHost: x\r\nContent-Length: {len}\r\n\r\n";

            Assert.Equal(403, (await Http11(port, Put("000000" == server.Code ? "111111" : "000000", rom.Length), rom)).Code);
            Assert.Equal(411, (await Http11(port, $"PUT /upload?code={server.Code} HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")).Code);
            Assert.Equal(413, (await Http11(port, Put(server.Code, 64L * 1024 * 1024))).Code);
            Assert.Equal(404, (await Http11(port, "GET /etc/passwd HTTP/1.1\r\n\r\n")).Code);

            var ok = await Http11(port, Put(server.Code, rom.Length), rom);
            Assert.Equal(200, ok.Code);
            Assert.True(ok.Body.Contains("\"status\":\"Added\"") && ok.Body.Contains("My Game"));
            var dup = await Http11(port, Put(server.Code, rom.Length), rom);
            Assert.True(dup.Body.Contains("AlreadyInLibrary"));
            var invalid = await Http11(port, Put(server.Code, 5), Encoding.ASCII.GetBytes("hello"));
            Assert.Equal(422, invalid.Code);
            Assert.Equal(1, lib.Games.Count);
            Assert.Equal(ImportChannel.Lan, lib.Games[0].Source);
            Assert.Equal(3, imported.Count);

            // Truncated body (client disconnects early) leaves nothing behind.
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, port);
                var head = Encoding.ASCII.GetBytes(Put(server.Code, 50000));
                await c.GetStream().WriteAsync(head, 0, head.Length);
                await c.GetStream().WriteAsync(Roms.Make(5), 0, 2000);
            }
            for (int i = 0; i < 50 && imported.Count < 4; i++) await Task.Delay(20);
            Assert.Equal(ImportStatus.SourceDisconnected, imported.Last().Status);
            Assert.Equal(0, FileCount(t.Path, "Temp"));

            Assert.True(LanUploadServer.IsLocalNetwork(IPAddress.Parse("192.168.1.20")) && LanUploadServer.IsLocalNetwork(IPAddress.Parse("10.1.2.3")) &&
                        LanUploadServer.IsLocalNetwork(IPAddress.Parse("172.20.0.1")) && LanUploadServer.IsLocalNetwork(IPAddress.Parse("fe80::1")));
            Assert.True(!LanUploadServer.IsLocalNetwork(IPAddress.Parse("8.8.8.8")) && !LanUploadServer.IsLocalNetwork(IPAddress.Parse("172.32.0.1")) &&
                        !LanUploadServer.IsLocalNetwork(IPAddress.Parse("2001:4860::1")));

            server.Stop();
            bool refused = false;
            try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, port); }
            catch (SocketException) { refused = true; }
            Assert.True(refused, "server stopped");
        }

        // ------------------------------------------------------------------ native bridge

        static void ConfigureNativeLibrary()
        {
            NativeLibrary.SetDllImportResolver(typeof(NesCore).Assembly, (name, asm, path) =>
            {
                if (name != "nescore") return IntPtr.Zero;
                string env = Environment.GetEnvironmentVariable("NESCORE_PATH");
                string dir = AppContext.BaseDirectory;
                var candidates = new List<string>();
                if (env != null) candidates.Add(env);
                for (int up = 0; up < 8 && dir != null; up++, dir = Path.GetDirectoryName(dir))
                    candidates.Add(Path.Combine(dir, "build", "linux", "libnescore.so"));
                foreach (var c in candidates)
                    if (File.Exists(c) && NativeLibrary.TryLoad(c, out var h)) return h;
                return IntPtr.Zero;
            });
        }

        static Task NativeCore()
        {
            NesCore core;
            try { core = new NesCore(); }
            catch (DllNotFoundException) { throw new SkipException("libnescore.so not built (cmake --build build/linux)"); }
            using (core)
            {
                Assert.True(core.LoadRom(Encoding.ASCII.GetBytes("garbage")) != null);
                Assert.Equal("Unsupported mapper", core.LoadRom(Roms.Make(1, mapper: 5)));
                Assert.Equal(null, core.LoadRom(Roms.Make(1, battery: true)));
                for (int i = 0; i < 3; i++) core.RunFrame();
                var frame = new byte[NesCore.FrameBytes(3)];
                Assert.True(core.CopyFrame(frame, 3));
                Assert.True(!core.CopyFrame(new byte[100], 3));
                var audio = new short[2048];
                int n = core.ReadAudio(audio);
                Assert.True(n > 700 && n < 900, "audio samples per frame: " + n);
                Assert.Equal(8192, core.ReadBattery().Length);
                core.WriteBattery(new byte[] { 0xAB });
                Assert.Equal(0xAB, core.ReadBattery()[0]);
                core.SetButtons(0, (byte)(NesButtons.A | NesButtons.Start));
                ulong h1 = core.FrameHash();
                using var other = new NesCore();
                other.LoadRom(Roms.Make(1, battery: true));
                for (int i = 0; i < 3; i++) other.RunFrame();
                Assert.Equal(h1, other.FrameHash());
            }
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------ live (opt-in)

        static async Task LiveHomebrewHub()
        {
            using var t = new TempDir();
            using var http = new SafeHttpClient();
            var client = new HomebrewHubCatalogClient(http, t.Sub("cache"));
            var all = new List<CatalogEntry>();
            var first = await client.GetPageAsync(1, null, default);
            all.AddRange(first.Entries);
            for (int p = 2; p <= Math.Min(first.PageTotal, 5); p++) all.AddRange((await client.GetPageAsync(p, null, default)).Entries);
            all = all.GroupBy(e => e.Slug).Select(g => g.First()).ToList();
            Console.WriteLine($"  live: {all.Count} NES entries, {all.Count(e => e.Downloadable)} downloadable: " +
                              string.Join(", ", all.Where(e => e.Downloadable).Select(e => e.Slug + " [" + e.License + "]")));
            Assert.True(all.Count > 0);
            var lib = await Open(t.Path);
            var svc = new RomDownloadService(http, lib);
            foreach (var e in all.Where(e => e.Downloadable).Take(2))
            {
                var r = await svc.DownloadAsync(e, null, default);
                Console.WriteLine($"  live download {e.Slug}: {r.Status} {r.Message} cover={(r.Game?.CoverPath ?? "none")}");
                Assert.True(r.Succeeded || r.Status == ImportStatus.UnsupportedMapper, r.Message);
            }
            var gh = await new GitHubReleaseCatalogClient(http).GetEntriesAsync(default);
            foreach (var e in gh) Console.WriteLine($"  live github {e.Slug}: downloadable={e.Downloadable} {e.RomUrl}");
            var thwaite = gh.First();
            if (thwaite.Downloadable)
            {
                var r = await svc.DownloadAsync(thwaite, null, default);
                Console.WriteLine($"  live github download: {r.Status} {r.Message}");
                Assert.True(r.Succeeded, r.Message);
            }
        }
    }
}
