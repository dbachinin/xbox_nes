using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NesXbox.Logic
{
    /// <summary>
    /// Minimal HTTP/1.1 receiver for LAN import. Runs only while the LAN import screen is open.
    ///   GET  /                         upload page for a phone/PC browser
    ///   PUT  /upload?name=X&amp;code=NNNNNN  raw .nes body (also POST), Content-Length required
    /// Accepts private/link-local peers only, one request at a time, bounded sizes and timeouts.
    /// </summary>
    public sealed class LanUploadServer : IDisposable
    {
        const int MaxHeaderBytes = 8192;
        static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);
        static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(120);

        readonly LocalRomLibrary library;
        readonly IPAddress bindAddress;
        readonly int requestedPort;
        TcpListener listener;
        CancellationTokenSource cts;

        public LanUploadServer(LocalRomLibrary library, IPAddress bindAddress, int port = 8765)
        {
            this.library = library;
            this.bindAddress = bindAddress;
            requestedPort = port;
            Code = NewCode();
        }

        public string Code { get; private set; }
        public int Port { get; private set; }
        public bool Running => listener != null;
        public event Action<ImportResult> Imported;

        static string NewCode()
        {
            var b = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return (BitConverter.ToUInt32(b, 0) % 1000000).ToString("D6");
        }

        public void Start()
        {
            if (listener != null) return;
            Code = NewCode();
            cts = new CancellationTokenSource();
            listener = new TcpListener(bindAddress, requestedPort);
            listener.Start(1);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var token = cts.Token;
            Task.Run(() => AcceptLoopAsync(listener, token));
        }

        public void Stop()
        {
            cts?.Cancel();
            listener?.Stop();
            listener = null;
        }

        public void Dispose() => Stop();

        async Task AcceptLoopAsync(TcpListener l, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await l.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception e) when (e is ObjectDisposedException || e is SocketException || e is InvalidOperationException) { return; }
                using (client)
                {
                    try { await HandleAsync(client, ct).ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException) { }
                }
            }
        }

        public static bool IsLocalNetwork(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            }
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = a.GetAddressBytes();
                return a.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
            }
            return false;
        }

        async Task HandleAsync(TcpClient client, CancellationToken serverCt)
        {
            if (!(client.Client.RemoteEndPoint is IPEndPoint remote) || !IsLocalNetwork(remote.Address)) return;
            var stream = client.GetStream();
            using (var reqCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt))
            using (reqCts.Token.Register(() => client.Dispose())) // aborts pending socket reads on timeout/stop
            {
                reqCts.CancelAfter(HeaderTimeout);
                var head = await ReadHeadAsync(stream, reqCts.Token).ConfigureAwait(false);
                if (head == null) { await RespondAsync(stream, 400, "text/plain", "Bad request").ConfigureAwait(false); return; }

                var lines = Encoding.ASCII.GetString(head.Value.Bytes, 0, head.Value.HeaderLength).Split(new[] { "\r\n" }, StringSplitOptions.None);
                var parts = lines[0].Split(' ');
                if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.")) { await RespondAsync(stream, 400, "text/plain", "Bad request").ConfigureAwait(false); return; }
                string method = parts[0], target = parts[1];
                long? length = null;
                bool chunked = false;
                for (int i = 1; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(':');
                    if (c <= 0) continue;
                    string k = lines[i].Substring(0, c).Trim(), v = lines[i].Substring(c + 1).Trim();
                    if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && long.TryParse(v, out long n) && n >= 0) length = n;
                    if (k.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) chunked = true;
                }

                string path = target, query = "";
                int q = target.IndexOf('?');
                if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }

                if (method == "GET" && path == "/") { await RespondAsync(stream, 200, "text/html; charset=utf-8", UploadPage).ConfigureAwait(false); return; }
                if ((method != "PUT" && method != "POST") || path != "/upload") { await RespondAsync(stream, 404, "text/plain", "Not found").ConfigureAwait(false); return; }
                if (!FixedTimeEquals(QueryValue(query, "code"), Code)) { await RespondJsonAsync(stream, 403, "Forbidden", "Wrong pairing code").ConfigureAwait(false); return; }
                if (chunked || length == null) { await RespondJsonAsync(stream, 411, "LengthRequired", "Content-Length required").ConfigureAwait(false); return; }
                if (length > InesHeader.MaxRomBytes) { await RespondJsonAsync(stream, 413, ImportStatus.TooLarge.ToString(), "File too large").ConfigureAwait(false); return; }

                string name = QueryValue(query, "name") ?? "upload.nes";
                reqCts.CancelAfter(BodyTimeout);
                var body = new PrefixedBoundedStream(head.Value.Bytes, head.Value.HeaderLength + 4, head.Value.Total, stream, length.Value);
                var result = await library.ImportAsync(body, name, new ImportOrigin { Channel = ImportChannel.Lan }, length, null, reqCts.Token)
                    .ConfigureAwait(false);
                Imported?.Invoke(result);
                int status = result.Succeeded ? 200 : 422;
                await RespondJsonAsync(stream, status, result.Status.ToString(), result.Message, result.Game?.Title).ConfigureAwait(false);
            }
        }

        struct Head { public byte[] Bytes; public int HeaderLength; public int Total; }

        static async Task<Head?> ReadHeadAsync(NetworkStream s, CancellationToken ct)
        {
            var buf = new byte[MaxHeaderBytes];
            int total = 0;
            while (total < buf.Length)
            {
                int n = await s.ReadAsync(buf, total, buf.Length - total, ct).ConfigureAwait(false);
                if (n <= 0) return null;
                int searchFrom = Math.Max(0, total - 3);
                total += n;
                for (int i = searchFrom; i + 3 < total; i++)
                    if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                        return new Head { Bytes = buf, HeaderLength = i, Total = total };
            }
            return null;
        }

        static string QueryValue(string query, string key)
        {
            foreach (var pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == key)
                {
                    try { return Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')); }
                    catch (UriFormatException) { return null; }
                }
            }
            return null;
        }

        static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        static Task RespondJsonAsync(Stream s, int code, string status, string message, string title = null)
        {
            string json = "{\"status\":\"" + JsonEscape(status) + "\",\"message\":\"" + JsonEscape(message) + "\"" +
                          (title != null ? ",\"title\":\"" + JsonEscape(title) + "\"" : "") + "}";
            return RespondAsync(s, code, "application/json", json);
        }

        static string JsonEscape(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static async Task RespondAsync(Stream s, int code, string contentType, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            string reason = code == 200 ? "OK" : code == 404 ? "Not Found" : code == 403 ? "Forbidden" : "Error";
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n" +
                                                 "Connection: close\r\nCache-Control: no-store\r\n\r\n");
            await s.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            await s.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await s.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>Request body = bytes already read past the header, then the socket, capped at Content-Length.</summary>
        sealed class PrefixedBoundedStream : Stream
        {
            readonly byte[] prefix;
            int prefixPos;
            readonly int prefixEnd;
            readonly Stream inner;
            long remaining;

            public PrefixedBoundedStream(byte[] prefix, int start, int end, Stream inner, long length)
            {
                this.prefix = prefix;
                prefixPos = start;
                prefixEnd = end;
                this.inner = inner;
                remaining = length;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (remaining <= 0) return 0;
                count = (int)Math.Min(count, remaining);
                int n;
                if (prefixPos < prefixEnd)
                {
                    n = Math.Min(count, prefixEnd - prefixPos);
                    Buffer.BlockCopy(prefix, prefixPos, buffer, offset, n);
                    prefixPos += n;
                }
                else
                {
                    n = await inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
                    if (n <= 0) throw new IOException("Connection closed before the full body arrived");
                }
                remaining -= n;
                return n;
            }

            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        const string UploadPage = @"<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width'>
<title>NES / Xbox upload</title><style>body{font-family:sans-serif;max-width:32em;margin:2em auto;padding:0 1em}
input,button{font-size:1.2em;margin:.3em 0}li{margin:.2em 0}</style></head><body>
<h1>Send .nes games to Xbox</h1><p>Enter the 6-digit code shown on the TV, choose files, press Upload.</p>
<p><input id=code inputmode=numeric maxlength=6 placeholder='Code'></p><p><input id=files type=file accept='.nes' multiple></p>
<p><button id=go>Upload</button></p><ul id=log></ul>
<script>
go.onclick=async()=>{for(const f of files.files){const li=document.createElement('li');li.textContent=f.name+': uploading…';log.appendChild(li);
try{const r=await fetch('/upload?code='+encodeURIComponent(code.value)+'&name='+encodeURIComponent(f.name),{method:'PUT',body:f});
const j=await r.json();li.textContent=f.name+': '+j.message;}catch(e){li.textContent=f.name+': '+e;}}};
</script></body></html>";
    }
}
