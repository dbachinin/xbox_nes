using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NesXbox.Logic;

namespace NesXbox.Logic.Tests
{
    static class Assert
    {
        public static void True(bool cond, string what = null, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (!cond) throw new Exception($"{Path.GetFileName(file)}:{line}: assertion failed {what}");
        }

        public static void Equal<T>(T expected, T actual, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{Path.GetFileName(file)}:{line}: expected <{expected}> got <{actual}>");
        }

        public static async Task<T> Throws<T>(Func<Task> f, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : Exception
        {
            try { await f(); }
            catch (T e) { return e; }
            throw new Exception($"{Path.GetFileName(file)}:{line}: expected {typeof(T).Name}");
        }
    }

    static class Roms
    {
        /// <summary>Valid 24 KiB NROM image; <paramref name="tag"/> makes the content (and SHA-256) unique.</summary>
        public static byte[] Make(int tag = 0, int mapper = 0, bool battery = false)
        {
            var rom = new byte[16 + 16384 + 8192];
            rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A;
            rom[4] = 1; rom[5] = 1;
            rom[6] = (byte)(((mapper & 0x0F) << 4) | (battery ? 2 : 0));
            rom[7] = (byte)(mapper & 0xF0);
            // PRG: infinite loop at $C000, reset vector -> $C000.
            rom[16] = 0x4C; rom[17] = 0x00; rom[18] = 0xC0;
            rom[16 + 0x3FFC] = 0x00; rom[16 + 0x3FFD] = 0xC0;
            BitConverter.GetBytes(tag).CopyTo(rom, 16 + 100);
            return rom;
        }
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nesxbox-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
        public string Sub(string name) { var p = System.IO.Path.Combine(Path, name); Directory.CreateDirectory(p); return p; }
    }

    /// <summary>In-memory folder tree standing in for a USB volume.</summary>
    sealed class FakeFolder : IScanFolder
    {
        public string Name { get; }
        public string DisplayPath { get; }
        public List<IScanFile> Files { get; } = new List<IScanFile>();
        public List<FakeFolder> Folders { get; } = new List<FakeFolder>();
        public Exception ListError { get; set; }

        public FakeFolder(string name, string parent = "") { Name = name; DisplayPath = parent + "/" + name; }

        public FakeFolder Folder(string name) { var f = new FakeFolder(name, DisplayPath); Folders.Add(f); return f; }
        public FakeFile File(string name, byte[] data) { var f = new FakeFile(name, DisplayPath + "/" + name, data); Files.Add(f); return f; }

        public Task<IReadOnlyList<IScanFile>> GetFilesAsync() =>
            ListError != null ? Task.FromException<IReadOnlyList<IScanFile>>(ListError) : Task.FromResult<IReadOnlyList<IScanFile>>(Files.ToList());

        public Task<IReadOnlyList<IScanFolder>> GetFoldersAsync() => Task.FromResult<IReadOnlyList<IScanFolder>>(Folders.Cast<IScanFolder>().ToList());
    }

    sealed class FakeFile : IScanFile
    {
        readonly byte[] data;
        public string Name { get; }
        public string DisplayPath { get; }
        public Exception OpenError { get; set; }
        public int FailAfterBytes { get; set; } = -1;  // simulate unplug mid-copy
        public int Opens { get; private set; }

        public FakeFile(string name, string path, byte[] data) { Name = name; DisplayPath = path; this.data = data; }
        public Task<long> GetSizeAsync() => Task.FromResult((long)data.Length);

        public Task<Stream> OpenReadAsync()
        {
            Opens++;
            if (OpenError != null && Opens > 1) return Task.FromException<Stream>(OpenError); // scan probe works, import fails
            Stream s = new MemoryStream(data, false);
            if (FailAfterBytes >= 0 && Opens > 1) s = new FailingStream(s, FailAfterBytes);
            return Task.FromResult(s);
        }
    }

    /// <summary>Throws IOException after N bytes, like a pulled USB stick.</summary>
    sealed class FailingStream : Stream
    {
        readonly Stream inner;
        long left;
        public FailingStream(Stream inner, long failAfter) { this.inner = inner; left = failAfter; }
        public override int Read(byte[] b, int o, int c)
        {
            if (left <= 0) throw new IOException("The device is not ready.");
            int n = inner.Read(b, o, (int)Math.Min(c, left));
            left -= n;
            return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>Endless zeros: proves the size cap without allocating 32 MiB.</summary>
    sealed class EndlessStream : Stream
    {
        public override int Read(byte[] b, int o, int c) { Array.Clear(b, o, c); if (c > 0) b[o] = (byte)'N'; return c; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>Stream that blocks forever after the first chunk until cancelled.</summary>
    sealed class StallingStream : Stream
    {
        readonly byte[] first;
        bool sent;
        public StallingStream(byte[] first) { this.first = first; }
        public override async Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct)
        {
            if (!sent) { sent = true; int n = Math.Min(c, first.Length); Array.Copy(first, 0, b, o, n); return n; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override int Read(byte[] b, int o, int c) => ReadAsync(b, o, c, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>Local HTTP fixture: routes by absolute URL; records requests.</summary>
    sealed class MockHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }
        public List<string> Requests { get; } = new List<string>();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri.AbsoluteUri);
            return Respond(request, ct);
        }

        public static HttpResponseMessage Bytes(byte[] b, string type = "application/octet-stream", long? declaredLength = null)
        {
            var c = new ByteArrayContent(b);
            c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            if (declaredLength.HasValue) c.Headers.ContentLength = declaredLength;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = c };
        }

        public static HttpResponseMessage Text(string s, string type = "application/json") =>
            Bytes(System.Text.Encoding.UTF8.GetBytes(s), type);

        public static HttpResponseMessage Status(HttpStatusCode code) => new HttpResponseMessage(code) { Content = new StringContent("") };

        public static HttpResponseMessage Redirect(string location)
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return r;
        }
    }
}
