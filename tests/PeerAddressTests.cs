using System;
using System.IO;
using System.Net;
using System.Threading;

namespace ScreenSwitch.Tests
{
    public static class PeerAddressTests
    {
        static string TempCachePath()
        {
            return Path.Combine(Path.GetTempPath(), "ss-peer-" + Guid.NewGuid() + ".txt");
        }

        public static void Test_Resolve_Success_ReturnsAddressAndWritesFile()
        {
            string cache = TempCachePath();
            try
            {
                var pa = new PeerAddress(delegate(string h) { return new IPAddress[] { IPAddress.Parse("192.168.0.10") }; }, 3000);
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                Assert.True(r.Ip != null, "ip resolved");
                Assert.Eq("192.168.0.10", r.Ip.ToString(), "resolved ip value");
                Assert.True(!r.FromCache, "not served from cache");
                Assert.True(File.Exists(cache), "cache file written");
                Assert.Eq("mac.local 192.168.0.10", File.ReadAllText(cache), "cache file contents");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Resolve_ThrowsException_FallsBackToLastKnown()
        {
            string cache = TempCachePath();
            try
            {
                File.WriteAllText(cache, "mac.local 10.0.0.9");
                var pa = new PeerAddress(delegate(string h) { throw new Exception("boom"); }, 3000);
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                Assert.True(r.Ip != null, "fallback ip present");
                Assert.Eq("10.0.0.9", r.Ip.ToString(), "fallback ip value");
                Assert.True(r.FromCache, "served from cache");
                Assert.True(r.Error != null && r.Error.Contains("boom"), "error captured: <" + r.Error + ">");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Resolve_OnlyFakeIp_TreatedAsFailure()
        {
            string cache = TempCachePath();
            try
            {
                var pa = new PeerAddress(delegate(string h) { return new IPAddress[] { IPAddress.Parse("198.18.0.5") }; }, 3000);
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                Assert.True(r.Ip == null, "fake-ip rejected and no cache -> null");
                Assert.True(!File.Exists(cache), "no cache written for fake-ip-only result");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Resolve_Timeout_ReturnsWithinDeadlineAndUsesLastKnown()
        {
            string cache = TempCachePath();
            try
            {
                File.WriteAllText(cache, "mac.local 10.0.0.9");
                var pa = new PeerAddress(delegate(string h) { Thread.Sleep(5000); return new IPAddress[] { IPAddress.Parse("192.168.1.1") }; }, 50);
                DateTime start = DateTime.UtcNow;
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                double elapsedMs = (DateTime.UtcNow - start).TotalMilliseconds;
                Assert.True(elapsedMs < 2000, "returned within deadline, took " + elapsedMs + " ms");
                Assert.True(r.Ip != null, "fallback ip present after timeout");
                Assert.Eq("10.0.0.9", r.Ip.ToString(), "fallback ip value after timeout");
                Assert.True(r.FromCache, "served from cache after timeout");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Resolve_Fails_NoCache_ReturnsNull()
        {
            string cache = TempCachePath();
            try
            {
                var pa = new PeerAddress(delegate(string h) { throw new Exception("no dns"); }, 3000);
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                Assert.True(r.Ip == null, "no cache and resolve failed -> null");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Resolve_CachedHostMismatch_Ignored()
        {
            string cache = TempCachePath();
            try
            {
                File.WriteAllText(cache, "other.local 10.0.0.9");
                var pa = new PeerAddress(delegate(string h) { throw new Exception("no dns"); }, 3000);
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                Assert.True(r.Ip == null, "cached entry for a different host is ignored");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Resolve_CachedHostMatchIsCaseInsensitive()
        {
            string cache = TempCachePath();
            try
            {
                File.WriteAllText(cache, "MAC.LOCAL 10.0.0.9");
                var pa = new PeerAddress(delegate(string h) { throw new Exception("no dns"); }, 3000);
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                Assert.True(r.Ip != null, "case-insensitive cached host match used");
                Assert.Eq("10.0.0.9", r.Ip.ToString(), "cached ip value");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Resolve_LiteralIp_SkipsResolverAndFile()
        {
            string cache = TempCachePath();
            bool called = false;
            var pa = new PeerAddress(delegate(string h) { called = true; return new IPAddress[0]; }, 3000);
            PeerResolveResult r = pa.Resolve("192.168.0.20", cache);
            Assert.True(!called, "resolver not invoked for an IPv4 literal host");
            Assert.True(r.Ip != null, "literal ip returned");
            Assert.Eq("192.168.0.20", r.Ip.ToString(), "literal ip value");
            Assert.True(!File.Exists(cache), "no cache file written for a literal ip");
        }

        public static void Test_Resolve_OnlyLoopbackOrAny_TreatedAsFailure()
        {
            string cache = TempCachePath();
            try
            {
                var pa = new PeerAddress(delegate(string h) { return new IPAddress[] { IPAddress.Parse("127.0.0.1"), IPAddress.Parse("0.0.0.0") }; }, 3000);
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                Assert.True(r.Ip == null, "loopback/0.0.0.0-only result treated as failure, no cache -> null");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Resolve_OnlyLinkLocal_TreatedAsFailure()
        {
            string cache = TempCachePath();
            try
            {
                var pa = new PeerAddress(delegate(string h) { return new IPAddress[] { IPAddress.Parse("169.254.10.20") }; }, 3000);
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                Assert.True(r.Ip == null, "169.254.0.0/16-only result treated as failure, no cache -> null");
                Assert.True(!File.Exists(cache), "no cache written for a link-local-only result");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Resolve_LinkLocalSkipped_UsesNextValid()
        {
            string cache = TempCachePath();
            try
            {
                var pa = new PeerAddress(delegate(string h) { return new IPAddress[] { IPAddress.Parse("169.254.1.1"), IPAddress.Parse("192.168.0.10") }; }, 3000);
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                Assert.Eq("192.168.0.10", r.Ip == null ? "(null)" : r.Ip.ToString(), "link-local skipped, next valid address used");
                Assert.Eq("mac.local 192.168.0.10", File.ReadAllText(cache), "cached the valid address");
                // edges of the range: 169.253.255.255 and 169.255.0.0 are outside 169.254.0.0/16
                var pa2 = new PeerAddress(delegate(string h) { return new IPAddress[] { IPAddress.Parse("169.254.255.255"), IPAddress.Parse("169.255.0.1") }; }, 3000);
                Assert.Eq("169.255.0.1", pa2.Resolve("mac.local", cache).Ip.ToString(), "169.254.255.255 rejected, 169.255.0.1 accepted");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Resolve_CachedLinkLocal_Ignored()
        {
            string cache = TempCachePath();
            try
            {
                File.WriteAllText(cache, "mac.local 169.254.3.4");
                var pa = new PeerAddress(delegate(string h) { throw new Exception("boom"); }, 3000);
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                Assert.True(r.Ip == null, "a link-local last-known address (e.g. cached by an older build) is not used");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Resolve_OnlyIPv6_TreatedAsFailure()
        {
            string cache = TempCachePath();
            try
            {
                var pa = new PeerAddress(delegate(string h) { return new IPAddress[] { IPAddress.Parse("fe80::1") }; }, 3000);
                PeerResolveResult r = pa.Resolve("mac.local", cache);
                Assert.True(r.Ip == null, "ipv6-only result treated as failure, no cache -> null");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        // 系统解析：第一种方法（DNS A 记录）给出可用的 IPv4 就用它；只有 IPv6、抛异常时换第二种（getaddrinfo）
        public static void Test_Fallback_UsesSecondWhenFirstHasNoIPv4()
        {
            bool secondCalled = false;
            Func<string, IPAddress[]> second = delegate(string h) { secondCalled = true; return new IPAddress[] { IPAddress.Parse("192.168.0.32") }; };
            var onlyV6 = PeerAddress.Fallback(delegate(string h) { return new IPAddress[] { IPAddress.Parse("fe80::1") }; }, second);
            Assert.Eq("192.168.0.32", onlyV6("mac.local")[0].ToString(), "ipv6-only first -> second");
            var throws = PeerAddress.Fallback(delegate(string h) { throw new Exception("rc=9003"); }, second);
            Assert.Eq("192.168.0.32", throws("mac.local")[0].ToString(), "throwing first -> second");
            secondCalled = false;
            var good = PeerAddress.Fallback(delegate(string h) { return new IPAddress[] { IPAddress.Parse("192.168.0.31") }; }, second);
            Assert.Eq("192.168.0.31", good("mac.local")[0].ToString(), "first with ipv4 wins");
            Assert.True(!secondCalled, "second not called when first has ipv4");
        }

        // 对方经 SSH 连进来时的来源地址（SSH_CONNECTION 第一段）记成最近一次的地址
        public static void Test_Learn_WritesClientAddressFromSshConnection()
        {
            string cache = TempCachePath();
            try
            {
                File.WriteAllText(cache, "mac.local 192.168.0.33");
                Assert.True(PeerAddress.Learn(cache, "mac.local", "192.168.0.31 51234 192.168.0.5 22"), "learned");
                Assert.Eq("mac.local 192.168.0.31", File.ReadAllText(cache), "cache replaced");
                var pa = new PeerAddress(delegate(string h) { throw new Exception("no dns"); }, 3000);
                Assert.Eq("192.168.0.31", pa.Resolve("mac.local", cache).Ip.ToString(), "resolve failure falls back to learned");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }

        public static void Test_Learn_IgnoresUnusableInput()
        {
            string cache = TempCachePath();
            try
            {
                Assert.True(!PeerAddress.Learn(cache, "mac.local", null), "null");
                Assert.True(!PeerAddress.Learn(cache, "mac.local", ""), "empty");
                Assert.True(!PeerAddress.Learn(cache, "mac.local", "garbage"), "garbage");
                Assert.True(!PeerAddress.Learn(cache, "mac.local", "127.0.0.1 5000 127.0.0.1 22"), "loopback");
                Assert.True(!PeerAddress.Learn(cache, "mac.local", "169.254.3.4 5000 192.168.0.5 22"), "link-local");
                Assert.True(!PeerAddress.Learn(cache, "mac.local", "fe80::1 5000 fe80::2 22"), "ipv6");
                Assert.True(!PeerAddress.Learn(cache, "192.168.0.9", "192.168.0.31 5000 192.168.0.5 22"), "peer host is an ip literal");
                Assert.True(!PeerAddress.Learn(cache, "", "192.168.0.31 5000 192.168.0.5 22"), "no peer host");
                Assert.True(!File.Exists(cache), "nothing written");
            }
            finally { if (File.Exists(cache)) File.Delete(cache); }
        }
    }
}
