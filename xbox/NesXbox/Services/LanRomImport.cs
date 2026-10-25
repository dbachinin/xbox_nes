using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using NesXbox.Logic;
using Windows.Networking;
using Windows.Networking.Connectivity;

namespace NesXbox.Services
{
    /// <summary>
    /// LAN fallback: hosts <see cref="LanUploadServer"/> on the console's private IPv4 address while the
    /// LAN import page is open. Requires privateNetworkClientServer. UNVERIFIED on Xbox hardware.
    /// </summary>
    public sealed class LanRomImport : IDisposable
    {
        public const int Port = 8765;
        LanUploadServer server;

        public string Address { get; private set; }
        public string Code => server?.Code;
        public string Url => Address == null ? null : "http://" + Address + ":" + Port + "/";
        public event Action<ImportResult> Imported;

        /// <summary>Returns null on success, otherwise a user-facing error.</summary>
        public string Start()
        {
            var ip = FindLanAddress();
            if (ip == null) return "No local network connection found. Connect the Xbox to your home network (Settings > Network).";
            try
            {
                server = new LanUploadServer(AppServices.Library, ip, Port);
                server.Imported += r => Imported?.Invoke(r);
                server.Start();
                Address = ip.ToString();
                return null;
            }
            catch (SocketException e)
            {
                server = null;
                return "Cannot listen on port " + Port + ": " + e.Message +
                       ". Use the Device Portal procedure in docs/USB_IMPORT.md instead.";
            }
        }

        public void Stop()
        {
            server?.Stop();
            server = null;
        }

        public void Dispose() => Stop();

        /// <summary>First private IPv4 of a connected adapter; we never bind to all interfaces.</summary>
        static IPAddress FindLanAddress()
        {
            foreach (var host in NetworkInformation.GetHostNames().Where(h => h.Type == HostNameType.Ipv4 && h.IPInformation != null))
            {
                if (IPAddress.TryParse(host.CanonicalName, out var ip) && LanUploadServer.IsLocalNetwork(ip) && !IPAddress.IsLoopback(ip))
                    return ip;
            }
            return null;
        }
    }
}
