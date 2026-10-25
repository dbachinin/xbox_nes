using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NesXbox.Logic
{
    public enum NetErrorKind { Offline, Timeout, Blocked, NotFound, ServerError, BadResponse }

    public sealed class NetException : Exception
    {
        public NetErrorKind Kind { get; }
        public NetException(NetErrorKind kind, string message, Exception inner = null) : base(message, inner) { Kind = kind; }
        public bool Transient => Kind == NetErrorKind.Offline || Kind == NetErrorKind.Timeout || Kind == NetErrorKind.ServerError;
    }

    /// <summary>HTTPS-only host allowlist. Catalog JSON can never make the app fetch anything else.</summary>
    public sealed class UrlPolicy
    {
        readonly HashSet<string> hosts;

        public UrlPolicy(IEnumerable<string> allowedHosts)
        {
            hosts = new HashSet<string>(allowedHosts.Select(h => h.ToLowerInvariant()));
        }

        public static UrlPolicy Default { get; } = new UrlPolicy(new[]
        {
            "hh3.gbdev.io",                        // Homebrew Hub API
            "raw.githubusercontent.com",           // homebrew-db dataset files, release redirects
            "api.github.com",                      // curated release listings
            "github.com",                          // browser_download_url (redirects to the CDN below)
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com",
        });

        public bool IsAllowed(Uri u) =>
            u != null && u.IsAbsoluteUri && u.Scheme == "https" && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo) &&
            hosts.Contains(u.Host.ToLowerInvariant());
    }

    /// <summary>
    /// GET with a validated manual redirect chain, per-request timeout and capped exponential backoff.
    /// The handler passed in must have automatic redirects disabled.
    /// </summary>
    public sealed class SafeHttpClient : IDisposable
    {
        const int MaxRedirects = 5;
        readonly HttpClient client;
        readonly UrlPolicy policy;

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
        public int MaxAttempts { get; set; } = 3;
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        public SafeHttpClient(HttpMessageHandler handler = null, UrlPolicy policy = null)
        {
            client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, true)
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan, // enforced per request below
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NesXbox/0.1 (+https://github.com/)");
            this.policy = policy ?? UrlPolicy.Default;
        }

        public UrlPolicy Policy => policy;

        /// <summary>Returns a successful response (caller disposes). Throws NetException otherwise.</summary>
        public async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { return await GetOnceAsync(url, ct).ConfigureAwait(false); }
                catch (NetException e) when (e.Transient && attempt < MaxAttempts && !ct.IsCancellationRequested)
                {
                    var delay = TimeSpan.FromTicks(Math.Min(BaseDelay.Ticks << (attempt - 1), TimeSpan.FromSeconds(8).Ticks));
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }

        public async Task<string> GetStringAsync(Uri url, long maxBytes, CancellationToken ct)
        {
            using (var resp = await GetAsync(url, ct).ConfigureAwait(false))
            {
                if (resp.Content.Headers.ContentLength > maxBytes) throw new NetException(NetErrorKind.BadResponse, "Response too large");
                var bytes = await ReadBoundedAsync(resp, maxBytes, ct).ConfigureAwait(false);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }

        public async Task<byte[]> GetBytesAsync(Uri url, long maxBytes, CancellationToken ct)
        {
            using (var resp = await GetAsync(url, ct).ConfigureAwait(false))
            {
                if (resp.Content.Headers.ContentLength > maxBytes) throw new NetException(NetErrorKind.BadResponse, "Response too large");
                return await ReadBoundedAsync(resp, maxBytes, ct).ConfigureAwait(false);
            }
        }

        static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage resp, long maxBytes, CancellationToken ct)
        {
            using (var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var ms = new System.IO.MemoryStream())
            {
                var buf = new byte[16384];
                int n;
                while ((n = await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + n > maxBytes) throw new NetException(NetErrorKind.BadResponse, "Response too large");
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
        }

        async Task<HttpResponseMessage> GetOnceAsync(Uri url, CancellationToken ct)
        {
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!policy.IsAllowed(url)) throw new NetException(NetErrorKind.Blocked, "Blocked URL: " + Describe(url));
                HttpResponseMessage resp;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(Timeout);
                    try
                    {
                        resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new NetException(NetErrorKind.Timeout, "Request timed out");
                    }
                    catch (HttpRequestException e)
                    {
                        throw new NetException(NetErrorKind.Offline, "Network unavailable: " + e.Message, e);
                    }
                }
                int code = (int)resp.StatusCode;
                if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                {
                    var loc = resp.Headers.Location;
                    resp.Dispose();
                    if (loc == null) throw new NetException(NetErrorKind.BadResponse, "Redirect without Location");
                    url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                    continue;
                }
                if (resp.IsSuccessStatusCode) return resp;
                resp.Dispose();
                if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Gone)
                    throw new NetException(NetErrorKind.NotFound, "Not found (HTTP " + code + ")");
                if (code >= 500 || code == 429) throw new NetException(NetErrorKind.ServerError, "Server error (HTTP " + code + ")");
                throw new NetException(NetErrorKind.BadResponse, "HTTP " + code);
            }
            throw new NetException(NetErrorKind.Blocked, "Too many redirects");
        }

        static string Describe(Uri u) => u == null ? "(none)" : u.Scheme + "://" + u.Host;

        public void Dispose() => client.Dispose();
    }
}
