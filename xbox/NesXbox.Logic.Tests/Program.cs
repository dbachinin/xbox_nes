using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NesXbox.Logic;

namespace NesXbox.Logic.Tests
{
    static partial class Program
    {
        static async Task<int> Main(string[] args)
        {
            ConfigureNativeLibrary();
            var tests = new List<(string Name, Func<Task> Run)>
            {
                // Library / import pipeline (M1, section 12)
                ("library_ten_roms_restart_favorite_sort_delete", LibraryTenRoms),
                ("library_duplicate_across_sources", LibraryDuplicateAcrossSources),
                ("library_invalid_and_unsupported", LibraryInvalidAndUnsupported),
                ("library_too_large_and_incomplete", LibraryTooLargeAndIncomplete),
                ("library_cancel_leaves_nothing", LibraryCancel),
                ("library_corrupt_metadata_recovery", LibraryCorruptMetadata),
                ("library_missing_rom_file", LibraryMissingRom),
                ("library_150_cards_and_query", LibraryManyCards),
                ("library_titles_sanitized", LibraryTitles),
                ("library_cover_and_saves", LibraryCoverAndSaves),
                // USB source via IRomSource fakes
                ("usb_no_drive_and_empty_drive", UsbNoDriveAndEmpty),
                ("usb_scan_layout_and_limits", UsbScanLayout),
                ("usb_import_one_then_unplug_still_listed", UsbImportOneThenUnplug),
                ("usb_hung_device_times_out", UsbHungDevice),
                ("usb_permission_failure", UsbPermissionFailure),
                ("usb_unplug_mid_copy", UsbUnplugMidCopy),
                ("usb_directory_folder", UsbDirectoryFolder),
                // Catalog (M1.5, section 13.3)
                ("catalog_filters_platform_and_rights", CatalogFilters),
                ("catalog_download_progress_offline_restart", CatalogDownload),
                ("catalog_cancel_retry_dedupe", CatalogCancelRetry),
                ("catalog_error_cases", CatalogErrors),
                ("catalog_offline_uses_cache", CatalogOfflineCache),
                ("catalog_redirect_policy", CatalogRedirectPolicy),
                ("catalog_path_safety", CatalogPathSafety),
                ("github_curated_provider", GitHubProvider),
                ("minijson_edge_cases", MiniJsonEdgeCases),
                // LAN fallback
                ("lan_upload_server", LanUpload),
                // Native bridge (same P/Invoke code as the Xbox app)
                ("native_core_pinvoke", NativeCore),
            };
            if (Environment.GetEnvironmentVariable("NES_LIVE") == "1") tests.Add(("live_homebrewhub_smoke", LiveHomebrewHub));

            var filter = args.Length > 0 ? args[0] : null;
            int failed = 0, ran = 0;
            foreach (var (name, run) in tests)
            {
                if (filter != null && !name.Contains(filter)) continue;
                ran++;
                var sw = Stopwatch.StartNew();
                try
                {
                    await run();
                    Console.WriteLine($"[PASS] {name} ({sw.ElapsedMilliseconds} ms)");
                }
                catch (SkipException s) { Console.WriteLine($"[SKIP] {name}: {s.Message}"); }
                catch (Exception e)
                {
                    failed++;
                    Console.WriteLine($"[FAIL] {name}: {e.Message}\n{e.StackTrace}");
                }
            }
            Console.WriteLine($"{ran - failed}/{ran} passed");
            return failed == 0 ? 0 : 1;
        }

        sealed class SkipException : Exception { public SkipException(string m) : base(m) { } }

        static ImportOrigin Usb => new ImportOrigin { Channel = ImportChannel.Usb };

        static async Task<ImportResult> Import(LocalRomLibrary lib, byte[] rom, string name, ImportOrigin o = null) =>
            await lib.ImportAsync(new MemoryStream(rom), name, o ?? Usb, rom.Length);

        static async Task<LocalRomLibrary> Open(string dir)
        {
            var lib = new LocalRomLibrary(dir);
            await lib.LoadAsync();
            return lib;
        }

        static int FileCount(string dir, string sub) => Directory.GetFiles(Path.Combine(dir, sub)).Length;

        // ------------------------------------------------------------------ library

        static async Task LibraryTenRoms()
        {
            using var t = new TempDir();
            var drive = new FakeFolder("USB");
            var nes = drive.Folder("NES");
            for (int i = 0; i < 10; i++) nes.File($"Game_{i:D2}.nes", Roms.Make(i));
            var scan = await new RomScanner(() => Task.FromResult<IReadOnlyList<IScanFolder>>(new[] { drive })).ScanAsync(default);
            Assert.Equal(10, scan.Candidates.Count);

            var lib = await Open(t.Path);
            var progress = new List<string>();
            var results = await BatchImporter.ImportAsync(lib, scan.Candidates, ImportChannel.Usb, new SyncProgress<BatchProgress>(p => progress.Add(p.Text)));
            Assert.True(results.All(r => r.Result.Status == ImportStatus.Added));
            Assert.True(progress.Contains("Importing 2/10"));

            // Restart: 10 cards, offline, from disk only.
            lib = await Open(t.Path);
            Assert.Equal(10, lib.Games.Count);
            Assert.True(lib.Games.All(g => g.Playable && g.Source == "usb" && g.Mapper == 0));
            var g3 = lib.Games.First(g => g.Title == "Game 03");
            await lib.SetFavoriteAsync(g3.Id, true);
            await lib.MarkPlayedAsync(g3.Id);
            var favs = GalleryQuery.Apply(lib.Games, GalleryFilter.Favorites, GallerySort.Title, null);
            Assert.Equal(1, favs.Count);
            var sorted = GalleryQuery.Apply(lib.Games, GalleryFilter.All, GallerySort.Title, null);
            Assert.Equal("Game 00", sorted[0].Title);
            Assert.Equal("Game 09", sorted[9].Title);
            Assert.Equal(g3.Id, GalleryQuery.Apply(lib.Games, GalleryFilter.RecentlyPlayed, GallerySort.LastPlayed, null).Single().Id);

            // Open the ROM bytes as the game page would.
            Assert.Equal(16 + 16384 + 8192, lib.ReadRom(g3).Length);

            await lib.DeleteAsync(sorted[0].Id);
            lib = await Open(t.Path);
            Assert.Equal(9, lib.Games.Count);
            Assert.Equal(9, FileCount(t.Path, "Games"));
            Assert.True(lib.Find(g3.Id).Favorite);
            Assert.True(lib.Find(g3.Id).LastPlayedAtUtc.HasValue);
            Assert.Equal(0, FileCount(t.Path, "Temp"));
        }

        static async Task LibraryDuplicateAcrossSources()
        {
            using var t = new TempDir();
            var lib = await Open(t.Path);
            var rom = Roms.Make(7);
            Assert.Equal(ImportStatus.Added, (await Import(lib, rom, "game.nes")).Status);
            var dupLan = await Import(lib, rom, "renamed.nes", new ImportOrigin { Channel = ImportChannel.Lan });
            Assert.Equal(ImportStatus.AlreadyInLibrary, dupLan.Status);
            Assert.Equal("Already in library", dupLan.Message);
            var dupCatalog = await Import(lib, rom, "x.nes", new ImportOrigin { Channel = ImportChannel.HomebrewHub, Title = "Other" });
            Assert.Equal(ImportStatus.AlreadyInLibrary, dupCatalog.Status);
            Assert.Equal(1, lib.Games.Count);
            Assert.Equal("game", lib.Games[0].Title);
            Assert.Equal(1, FileCount(t.Path, "Games"));
            Assert.Equal(0, FileCount(t.Path, "Temp"));
        }

        static async Task LibraryInvalidAndUnsupported()
        {
            using var t = new TempDir();
            var lib = await Open(t.Path);
            await Import(lib, Roms.Make(1), "keep.nes");
            var junk = Encoding.ASCII.GetBytes("<html>not a rom</html>");
            Assert.Equal(ImportStatus.InvalidRom, (await Import(lib, junk, "bad.nes")).Status);
            var truncated = Roms.Make(2).Take(5000).ToArray();
            Assert.Equal(ImportStatus.InvalidRom, (await Import(lib, truncated, "short.nes")).Status);
            var mmc5 = await Import(lib, Roms.Make(3, mapper: 5), "mmc5.nes");
            Assert.Equal(ImportStatus.UnsupportedMapper, mmc5.Status);
            Assert.True(mmc5.Message.Contains("mapper 5"));
            Assert.Equal(1, lib.Games.Count);
            Assert.Equal(1, FileCount(t.Path, "Games"));
            Assert.Equal(0, FileCount(t.Path, "Temp"));
        }

        static async Task LibraryTooLargeAndIncomplete()
        {
            using var t = new TempDir();
            var lib = await Open(t.Path);
            Assert.Equal(ImportStatus.TooLarge, (await lib.ImportAsync(new EndlessStream(), "huge.nes", Usb)).Status);
            Assert.Equal(ImportStatus.TooLarge, (await lib.ImportAsync(new MemoryStream(new byte[10]), "x.nes", Usb, 64L * 1024 * 1024)).Status);
            var rom = Roms.Make(1);
            Assert.Equal(ImportStatus.Incomplete, (await lib.ImportAsync(new MemoryStream(rom), "x.nes", Usb, rom.Length + 100)).Status);
            Assert.Equal(0, lib.Games.Count);
            Assert.Equal(0, FileCount(t.Path, "Temp"));
        }

        static async Task LibraryCancel()
        {
            using var t = new TempDir();
            var lib = await Open(t.Path);
            using var cts = new CancellationTokenSource(200);
            var r = await lib.ImportAsync(new StallingStream(Roms.Make(1).Take(1000).ToArray()), "slow.nes", Usb, null, null, cts.Token);
            Assert.Equal(ImportStatus.Cancelled, r.Status);
            Assert.Equal(0, lib.Games.Count);
            Assert.Equal(0, FileCount(t.Path, "Temp"));
            Assert.Equal(0, FileCount(t.Path, "Games"));
        }

        static async Task LibraryCorruptMetadata()
        {
            using var t = new TempDir();
            var lib = await Open(t.Path);
            await Import(lib, Roms.Make(1), "one.nes");
            await Import(lib, Roms.Make(2), "two.nes");  // second save leaves library.bak with "one"
            await lib.SetFavoriteAsync(lib.Games[0].Id, true);

            File.WriteAllText(Path.Combine(t.Path, "library.json"), "{\"schema_version\":1,\"games\":[{\"id\":");  // partial write
            lib = await Open(t.Path);
            Assert.True(lib.RecoveredFromCorruption);
            Assert.Equal(2, lib.Games.Count);   // backup + orphan scan
            Assert.True(File.Exists(Path.Combine(t.Path, "library.corrupt.json")));

            // Total metadata loss: everything is rebuilt from Games/.
            File.Delete(Path.Combine(t.Path, "library.json"));
            File.Delete(Path.Combine(t.Path, "library.bak"));
            lib = await Open(t.Path);
            Assert.Equal(2, lib.Games.Count);
            Assert.True(lib.Games.All(g => g.Playable));

            // Hostile metadata: bad hashes and path traversal are dropped/normalised.
            File.WriteAllText(Path.Combine(t.Path, "library.json"),
                "{\"schema_version\":1,\"games\":[{\"sha256\":\"../../etc\",\"title\":\"x\"}," +
                "{\"sha256\":\"" + lib.Games[0].Sha256 + "\",\"rom_path\":\"../../../evil.nes\",\"cover_path\":\"../../x.png\",\"title\":\"ok\"}]}");
            lib = await Open(t.Path);
            var ok = lib.Games.Single(g => g.Title == "ok");
            Assert.Equal("Games/" + ok.Sha256 + ".nes", ok.RomPath);
            Assert.Equal(null, ok.CoverPath);
            Assert.Equal(2, lib.Games.Count);   // the other ROM is re-registered as an orphan
        }

        static async Task LibraryMissingRom()
        {
            using var t = new TempDir();
            var lib = await Open(t.Path);
            var g = (await Import(lib, Roms.Make(1), "gone.nes")).Game;
            File.Delete(lib.GetRomPath(g));
            lib = await Open(t.Path);
            var e = lib.Find(g.Id);
            Assert.True(!e.Available && !e.Playable);
            Assert.Equal("File missing", GalleryQuery.StatusOf(e));
            // Re-importing the same bytes repairs the entry.
            Assert.Equal(ImportStatus.AlreadyInLibrary, (await Import(lib, Roms.Make(1), "gone.nes")).Status);
            Assert.True(lib.Find(g.Id).Available);
            Assert.True(File.Exists(lib.GetRomPath(g)));
        }

        static async Task LibraryManyCards()
        {
            using var t = new TempDir();
            var lib = await Open(t.Path);
            for (int i = 0; i < 150; i++) await Import(lib, Roms.Make(1000 + i), $"Title {i:D3}.nes");
            lib = await Open(t.Path);
            Assert.Equal(150, lib.Games.Count);
            var sw = Stopwatch.StartNew();
            var hits = GalleryQuery.Apply(lib.Games, GalleryFilter.All, GallerySort.Title, "title 14");
            Assert.True(sw.ElapsedMilliseconds < 100, "query fast");
            Assert.Equal(10, hits.Count); // 140..149
            Assert.Equal(GalleryQuery.RecentLimit, GalleryQuery.Apply(lib.Games, GalleryFilter.RecentlyAdded, GallerySort.DateAdded, null).Count);
            var byDate = GalleryQuery.Apply(lib.Games, GalleryFilter.All, GallerySort.DateAdded, null);
            Assert.Equal(150, byDate.Count);
            Assert.True(GalleryQuery.PlaceholderColor(lib.Games[0]) <= 0xFFFFFF);
            Assert.Equal("Ready", GalleryQuery.StatusOf(lib.Games[0]));
        }

        static async Task LibraryTitles()
        {
            Assert.Equal("Super Game", LocalRomLibrary.TitleFromFileName("dir/sub\\Super_Game.NES"));
            Assert.Equal("Untitled game", LocalRomLibrary.TitleFromFileName(".nes"));
            Assert.Equal("a b", LocalRomLibrary.TitleFromFileName("a\u0000\u0007b.nes"));
            Assert.Equal(80, LocalRomLibrary.TitleFromFileName(new string('x', 500) + ".nes").Length);
            using var t = new TempDir();
            var lib = await Open(t.Path);
            var g = (await Import(lib, Roms.Make(1), "x.nes")).Game;
            await lib.RenameAsync(g.Id, "  New\tName  ");
            await lib.RenameAsync(g.Id, "   ");  // ignored
            Assert.Equal("New Name", (await Open(t.Path)).Find(g.Id).Title);
        }

        static async Task LibraryCoverAndSaves()
        {
            using var t = new TempDir();
            var lib = await Open(t.Path);
            var g = (await Import(lib, Roms.Make(1, battery: true), "save.nes")).Game;
            Assert.True(g.Battery);
            Assert.True(!await lib.SetCoverAsync(g.Id, Encoding.ASCII.GetBytes("not an image")));
            var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0 };
            Assert.True(await lib.SetCoverAsync(g.Id, png));
            lib.WriteSave(g, new byte[] { 1, 2, 3 });
            Assert.Equal(null, lib.ReadState(g));
            lib.WriteState(g, new byte[] { 9, 9 });
            lib.WriteState(g, new byte[] { 7, 7, 7 });   // overwrite the slot
            Assert.True(lib.StateSavedAtUtc(g).HasValue);
            lib = await Open(t.Path);
            var e = lib.Find(g.Id);
            Assert.Equal("Covers/" + g.Sha256 + ".png", e.CoverPath);
            Assert.True(File.Exists(lib.GetCoverPath(e)));
            Assert.Equal(3, lib.ReadSave(e).Length);
            Assert.Equal(3, lib.ReadState(e).Length);
            Assert.Equal(0, FileCount(t.Path, "Temp"));
            await lib.DeleteAsync(e.Id);
            Assert.True(!File.Exists(Path.Combine(t.Path, "Covers", g.Sha256 + ".png")));
            Assert.True(File.Exists(Path.Combine(t.Path, "Saves", g.Sha256 + ".sav")), "saves survive deletion");
        }

        // ------------------------------------------------------------------ USB

        static RomScanner Scanner(params IScanFolder[] volumes) =>
            new RomScanner(() => Task.FromResult<IReadOnlyList<IScanFolder>>(volumes));

        static async Task UsbNoDriveAndEmpty()
        {
            var none = await Scanner().ScanAsync(default);
            Assert.Equal(RomSourceError.NoUsbDrive, none.Error);
            Assert.Equal("No USB drive found", none.Message);
            var empty = new FakeFolder("E:");
            empty.Folder("Photos").File("cat.jpg", new byte[100]);
            var r = await Scanner(empty).ScanAsync(default);
            Assert.Equal(RomSourceError.None, r.Error);
            Assert.Equal(0, r.Candidates.Count);
            Assert.Equal(1, r.Volumes.Count);
            var denied = await new RomScanner(() => Task.FromException<IReadOnlyList<IScanFolder>>(new UnauthorizedAccessException("x"))).ScanAsync(default);
            Assert.Equal(RomSourceError.AccessDenied, denied.Error);
        }

        static async Task UsbScanLayout()
        {
            var v = new FakeFolder("E:");
            v.File("root.NES", Roms.Make(1));
            v.File("readme.txt", new byte[10]);
            v.Folder("NES").File("inner.nes", Roms.Make(2));
            v.Folder("NES").File("bad.nes", new byte[100]);
            v.Folder("System Volume Information").File("hidden.nes", Roms.Make(3));
            var deep = v.Folder("a").Folder("b").Folder("c");
            deep.File("depth3.nes", Roms.Make(4));
            deep.Folder("d").File("depth4.nes", Roms.Make(5));
            v.Folder("mmc5").File("m5.nes", Roms.Make(6, mapper: 5));

            var r = await Scanner(v).ScanAsync(default);
            var names = r.Candidates.Select(c => c.FileName).ToList();
            Assert.True(names.Contains("root.NES") && names.Contains("inner.nes") && names.Contains("depth3.nes"));
            Assert.True(!names.Contains("depth4.nes") && !names.Contains("hidden.nes") && !names.Contains("readme.txt"));
            Assert.Equal("inner.nes", names[1]);  // root first, then /NES
            Assert.Equal(RomCheck.InvalidRom, r.Candidates.Single(c => c.FileName == "bad.nes").Check);
            var m5 = r.Candidates.Single(c => c.FileName == "m5.nes");
            Assert.Equal("Unsupported mapper 5", m5.StatusText);
            Assert.True(!m5.Importable);

            var nonRecursive = await new RomScanner(() => Task.FromResult<IReadOnlyList<IScanFolder>>(new[] { v }), new ScanOptions { Recursive = false }).ScanAsync(default);
            Assert.Equal(3, nonRecursive.Candidates.Count); // root.NES + NES/inner.nes + NES/bad.nes

            var many = new FakeFolder("F:");
            for (int i = 0; i < 30; i++) many.File($"g{i}.nes", Roms.Make(i));
            var limited = await new RomScanner(() => Task.FromResult<IReadOnlyList<IScanFolder>>(new[] { many }), new ScanOptions { MaxFiles = 25 }).ScanAsync(default);
            Assert.Equal(25, limited.Candidates.Count);
            Assert.True(limited.Truncated);

            var pulled = new FakeFolder("G:") { ListError = new IOException("device removed") };
            var pr = await Scanner(pulled, v).ScanAsync(default);
            Assert.Equal(RomSourceError.DriveDisconnected, pr.Error);
            Assert.True(pr.Candidates.Count > 0, "other volumes still scanned");

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.Throws<OperationCanceledException>(() => Scanner(v).ScanAsync(cts.Token));
        }

        static async Task UsbHungDevice()
        {
            var v = new FakeFolder("E:");
            v.File("ok.nes", Roms.Make(1));
            v.Folder("NES").Hang = true;
            var seen = new List<string>();
            var scanner = new RomScanner(() => Task.FromResult<IReadOnlyList<IScanFolder>>(new[] { v }),
                                         new ScanOptions { Timeout = TimeSpan.FromMilliseconds(300) });
            var sw = Stopwatch.StartNew();
            var r = await scanner.ScanAsync(default, new SyncProgress<string>(seen.Add));
            Assert.True(sw.ElapsedMilliseconds < 3000, "returns instead of hanging");
            Assert.Equal(RomSourceError.Failed, r.Error);
            Assert.True(r.Message.Contains("timed out") && r.Message.Contains("/E:/NES"), r.Message);
            Assert.Equal(1, r.Candidates.Count);            // what was found before the hang is kept
            Assert.True(seen.Contains("/E:/NES"));

            // User cancel also works while a device call hangs.
            using var cts = new CancellationTokenSource(200);
            await Assert.Throws<OperationCanceledException>(() => new RomScanner(
                () => new TaskCompletionSource<IReadOnlyList<IScanFolder>>().Task).ScanAsync(cts.Token));
        }

        static async Task UsbImportOneThenUnplug()
        {
            using var t = new TempDir();
            var v = new FakeFolder("E:");
            v.Folder("NES").File("Alter Ego.nes", Roms.Make(42));
            var scan = await Scanner(v).ScanAsync(default);
            var lib = await Open(t.Path);
            var res = await BatchImporter.ImportAsync(lib, scan.Candidates, ImportChannel.Usb);
            Assert.Equal(ImportStatus.Added, res.Single().Result.Status);
            // "Unplug": the volume object is gone; a fresh library instance still lists the game.
            v = null;
            lib = await Open(t.Path);
            Assert.Equal("Alter Ego", lib.Games.Single().Title);
            Assert.True(lib.Games.Single().Playable);
        }

        static async Task UsbPermissionFailure()
        {
            using var t = new TempDir();
            var v = new FakeFolder("E:");
            v.File("locked.nes", Roms.Make(1)).OpenError = new UnauthorizedAccessException("denied");
            v.File("ok.nes", Roms.Make(2));
            var scan = await Scanner(v).ScanAsync(default);
            var lib = await Open(t.Path);
            var res = await BatchImporter.ImportAsync(lib, scan.Candidates, ImportChannel.Usb);
            Assert.Equal(ImportStatus.AccessDenied, res.Single(r => r.Candidate.FileName == "locked.nes").Result.Status);
            Assert.Equal(ImportStatus.Added, res.Single(r => r.Candidate.FileName == "ok.nes").Result.Status);
        }

        static async Task UsbUnplugMidCopy()
        {
            using var t = new TempDir();
            var lib = await Open(t.Path);
            await Import(lib, Roms.Make(100), "existing.nes");
            string before = File.ReadAllText(Path.Combine(t.Path, "library.json"));

            var v = new FakeFolder("E:");
            v.File("pulled.nes", Roms.Make(1)).FailAfterBytes = 9000;
            var scan = await Scanner(v).ScanAsync(default);
            var res = await BatchImporter.ImportAsync(lib, scan.Candidates, ImportChannel.Usb);
            Assert.Equal(ImportStatus.SourceDisconnected, res.Single().Result.Status);
            Assert.Equal(1, lib.Games.Count);
            Assert.Equal(before, File.ReadAllText(Path.Combine(t.Path, "library.json")));
            Assert.Equal(0, FileCount(t.Path, "Temp"));
            Assert.Equal(1, FileCount(t.Path, "Games"));
        }

        static async Task UsbDirectoryFolder()
        {
            using var t = new TempDir();
            var drive = t.Sub("drive");
            Directory.CreateDirectory(Path.Combine(drive, "NES"));
            File.WriteAllBytes(Path.Combine(drive, "NES", "a.nes"), Roms.Make(1));
            var original = File.ReadAllBytes(Path.Combine(drive, "NES", "a.nes"));
            var scan = await Scanner(new DirectoryScanFolder(drive)).ScanAsync(default);
            var lib = await Open(t.Sub("app"));
            var res = await BatchImporter.ImportAsync(lib, scan.Candidates, ImportChannel.Usb);
            Assert.Equal(ImportStatus.Added, res.Single().Result.Status);
            Assert.True(original.SequenceEqual(File.ReadAllBytes(Path.Combine(drive, "NES", "a.nes"))), "source untouched");
            Assert.Equal(1, Directory.GetFiles(drive, "*", SearchOption.AllDirectories).Length);
        }
    }
}
