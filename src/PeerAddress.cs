using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenSwitch
{
    // Result of resolving the Mac's ssh target address.
    // Ip is null when neither a fresh resolve nor a cached last-known address is available.
    public class PeerResolveResult
    {
        public readonly IPAddress Ip;
        public readonly bool FromCache;
        public readonly long ElapsedMs;
        public readonly string Error;

        public PeerResolveResult(IPAddress ip, bool fromCache, long elapsedMs, string error)
        {
            Ip = ip; FromCache = fromCache; ElapsedMs = elapsedMs; Error = error;
        }
    }

    // Resolves cfg.PeerHost to an IPv4 address with a bounded timeout, falling back to the
    // last address that resolved successfully (cached on disk) when resolution fails, times
    // out, or returns only invalid addresses (loopback, 0.0.0.0, IPv6, a proxy fake-ip, or
    // IPv4 link-local 169.254.0.0/16).
    public class PeerAddress
    {
        public const int DefaultTimeoutMs = 3000;

        readonly Func<string, IPAddress[]> resolver;
        readonly int timeoutMs;

        public PeerAddress() : this(null, DefaultTimeoutMs) { }

        public PeerAddress(Func<string, IPAddress[]> resolver) : this(resolver, DefaultTimeoutMs) { }

        public PeerAddress(Func<string, IPAddress[]> resolver, int timeoutMs)
        {
            this.resolver = resolver != null ? resolver : Fallback(DnsQueryA, Dns.GetHostAddresses);
            this.timeoutMs = timeoutMs;
        }

        // Tries first; when it throws or returns no usable IPv4 address, returns what second gives.
        // On this machine Dns.GetHostAddresses (getaddrinfo, any family) for the Mac's .local name
        // often returns only its IPv6 addresses or "host not found", while a DNS query for the A record
        // answers far more often; so the A query goes first and getaddrinfo is the backup. Both can fail
        // for a while together: then the last-known address is used (see Learn).
        public static Func<string, IPAddress[]> Fallback(Func<string, IPAddress[]> first, Func<string, IPAddress[]> second)
        {
            return delegate(string host)
            {
                try
                {
                    IPAddress[] a = first(host);
                    if (FirstValid(a) != null) return a;
                }
                catch (Exception) { }
                return second(host);
            };
        }

        [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, EntryPoint = "DnsQuery_W")]
        static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);
        [DllImport("dnsapi.dll")]
        static extern void DnsRecordListFree(IntPtr list, int freeType);

        const ushort DNS_TYPE_A = 1;
        const int DnsFreeRecordList = 1;

        // A records of host through the Windows DNS client (mDNS for .local names).
        // DNS_RECORD: pNext, pName, wType, wDataLength, Flags, dwTtl, dwReserved, then the data
        // (for A: the IPv4 address in network order).
        public static IPAddress[] DnsQueryA(string host)
        {
            IntPtr list;
            int rc = DnsQuery(host, DNS_TYPE_A, 0, IntPtr.Zero, out list, IntPtr.Zero);
            if (rc != 0) throw new Win32Exception(rc);
            var result = new List<IPAddress>();
            try
            {
                for (IntPtr p = list; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p))
                {
                    if ((ushort)Marshal.ReadInt16(p, IntPtr.Size * 2) != DNS_TYPE_A) continue;
                    byte[] b = new byte[4];
                    Marshal.Copy(new IntPtr(p.ToInt64() + IntPtr.Size * 2 + 16), b, 0, 4);
                    result.Add(new IPAddress(b));
                }
            }
            finally { DnsRecordListFree(list, DnsFreeRecordList); }
            return result.ToArray();
        }

        // The peer just connected to us over SSH: SSH_CONNECTION is "client_ip client_port server_ip server_port".
        // Its client address is where the peer is right now, a fresher last-known address than an old
        // resolve (its DHCP address changes). Written to the same cache the resolve falls back to.
        // Returns whether the cache was written.
        public static bool Learn(string cacheFilePath, string host, string sshConnection)
        {
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(sshConnection)) return false;
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal)) return false;
            string[] parts = sshConnection.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            IPAddress ip;
            if (parts.Length < 1 || !IPAddress.TryParse(parts[0], out ip) || !IsValidCandidate(ip)) return false;
            return WriteCache(cacheFilePath, host, ip);
        }

        public PeerResolveResult Resolve(string host, string cacheFilePath)
        {
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal) && literal.AddressFamily == AddressFamily.InterNetwork)
            {
                // Already an IPv4 literal: nothing to resolve, nothing to cache.
                return new PeerResolveResult(literal, false, 0, null);
            }

            Stopwatch sw = Stopwatch.StartNew();
            IPAddress[] addrs = null;
            string error = null;

            IAsyncResult ar = resolver.BeginInvoke(host, null, null);
            if (ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                try { addrs = resolver.EndInvoke(ar); }
                catch (Exception e) { error = e.Message; }
            }
            else
            {
                error = "timed out after " + timeoutMs + " ms";
                // The lookup may still complete later on its own thread-pool thread; let it
                // finish and release the async handle without blocking this call any further.
                Func<string, IPAddress[]> pending = resolver;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { ar.AsyncWaitHandle.WaitOne(); pending.EndInvoke(ar); }
                    catch (Exception) { }
                });
            }
            sw.Stop();

            IPAddress valid = FirstValid(addrs);
            if (valid == null && error == null) error = "no usable address in DNS response";

            if (valid != null)
            {
                WriteCache(cacheFilePath, host, valid);
                return new PeerResolveResult(valid, false, sw.ElapsedMilliseconds, null);
            }

            IPAddress cached = ReadCache(cacheFilePath, host);
            return new PeerResolveResult(cached, cached != null, sw.ElapsedMilliseconds, error);
        }

        static IPAddress FirstValid(IPAddress[] addrs)
        {
            if (addrs == null) return null;
            for (int i = 0; i < addrs.Length; i++)
            {
                if (IsValidCandidate(addrs[i])) return addrs[i];
            }
            return null;
        }

        static bool IsValidCandidate(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            if (IPAddress.IsLoopback(ip)) return false;
            if (ip.Equals(IPAddress.Any)) return false;
            if (IsFakeIp(ip)) return false;
            if (IsLinkLocal(ip)) return false;
            return true;
        }

        // Proxy fake-ip range used by tools such as Mihomo in TUN mode: 198.18.0.0/15
        // (198.18.0.0 - 198.19.255.255). An address only in this range is not the real Mac.
        static bool IsFakeIp(IPAddress ip)
        {
            return InRange(ip, 198, 18, 0xFFFE0000);
        }

        // IPv4 link-local 169.254.0.0/16: a self-assigned address (no DHCP lease), not the
        // Mac's LAN address.
        static bool IsLinkLocal(IPAddress ip)
        {
            return InRange(ip, 169, 254, 0xFFFF0000);
        }

        static bool InRange(IPAddress ip, uint a, uint b, uint mask)
        {
            byte[] x = ip.GetAddressBytes();
            uint v = ((uint)x[0] << 24) | ((uint)x[1] << 16) | ((uint)x[2] << 8) | x[3];
            uint network = (a << 24) | (b << 16);
            return (v & mask) == (network & mask);
        }

        static bool WriteCache(string path, string host, IPAddress ip)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, host + " " + ip);
                File.Copy(tmp, path, true);
                File.Delete(tmp);
                return true;
            }
            catch (Exception) { return false; }
        }

        static IPAddress ReadCache(string path, string host)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path).Trim();
                int sp = text.IndexOf(' ');
                if (sp <= 0 || sp >= text.Length - 1) return null;
                string cachedHost = text.Substring(0, sp);
                string cachedIp = text.Substring(sp + 1).Trim();
                if (!string.Equals(cachedHost, host, StringComparison.OrdinalIgnoreCase)) return null;
                IPAddress ip;
                if (!IPAddress.TryParse(cachedIp, out ip)) return null;
                // Same rules as a fresh result (e.g. a link-local address cached by an older build).
                if (!IsValidCandidate(ip)) return null;
                return ip;
            }
            catch (Exception) { return null; }
        }
    }
}
