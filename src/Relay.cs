using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScreenSwitch
{
    // 经 SSH 调用对方的程序（设计文档 5.2）。返回值都是对端的退出码（连不上、超时为 255）。
    public interface IRelay
    {
        // 请对方把主屏切给本机：request give peer；对方是旧版本（退出码 1）时用旧写法重试一次
        int RequestGivePeer();
        // 请对方把主屏切到编码为 code 的设备：request give code <n>
        int RequestGiveCode(uint code);
        // 切给对方之前通知它预先恢复布局（设计文档 5.2）；结果只供记日志
        int RequestTake();
        // request info：取对方的编码、名字、设备列表；取不到（连不上、对方是旧版本）返回 null
        PeerInfo RequestInfo();
    }

    public class SshRelay : IRelay
    {
        // 运行 ssh：参数、超时毫秒、标准输出、标准错误 → 退出码。测试里换成假的。
        public delegate int Runner(string args, int timeoutMs, out string stdout, out string stderr);

        // 对方最近的地址：解析成功时记下，对方经 ssh-gate 连进来时也记下（见 Cli.SshGate）
        public const string PeerAddressFile = "peer-address.txt";

        readonly Config cfg;
        readonly Log log;
        readonly PeerAddress peerAddress;
        readonly string peerAddressCachePath;
        readonly Runner runner;

        public SshRelay(Config cfg, Log log) : this(cfg, log, AppDomain.CurrentDomain.BaseDirectory) { }

        public SshRelay(Config cfg, Log log, string dir) : this(cfg, log, dir, null) { }

        public SshRelay(Config cfg, Log log, string dir, Runner runner)
        {
            this.cfg = cfg; this.log = log;
            this.peerAddress = new PeerAddress();
            this.peerAddressCachePath = Path.Combine(dir, PeerAddressFile);
            this.runner = runner ?? RunSshExe;
        }

        int RunSshExe(string args, int timeoutMs, out string stdout, out string stderr)
        {
            return RunProcess(cfg.SshExe, args, timeoutMs, out stdout, out stderr);
        }

        static string Quote(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        // request：request 后面的部分，例如 "give peer"、"give code 17"、"take"、"info"
        public static string BuildArgs(Config cfg, string request)
        {
            return BuildArgs(cfg, request, null);
        }

        public static string BuildArgs(Config cfg, string request, string ip)
        {
            string remote = cfg.PeerProgramPath + " request " + request;
            string addr = string.IsNullOrEmpty(ip) ? "" : " -o HostName=" + ip + " -o HostKeyAlias=" + cfg.PeerHost;
            string key = string.IsNullOrEmpty(cfg.PeerKeyPath) ? "" : " -i " + Quote(cfg.PeerKeyPath);
            return "-o BatchMode=yes -o ConnectTimeout=" + cfg.PeerTimeoutSec + addr + key + " " + cfg.PeerUser + "@" + cfg.PeerHost + " " + Quote(remote);
        }

        // 调用方最多等 ConnectTimeout + 2 秒（设计文档 5.2）：解析对方地址用掉的时间从 ssh 的超时里扣掉，至少留 1 秒。
        public static int TakeSshTimeoutMs(Config cfg, long resolveElapsedMs)
        {
            long left = (cfg.PeerTimeoutSec + 2) * 1000L - resolveElapsedMs;
            return (int)Math.Max(1000, left);
        }

        public static int RunProcess(string exe, string args, int timeoutMs)
        {
            string stdout, stderr;
            return RunProcess(exe, args, timeoutMs, out stdout, out stderr);
        }

        public static int RunProcess(string exe, string args, int timeoutMs, out string stderr)
        {
            string stdout;
            return RunProcess(exe, args, timeoutMs, out stdout, out stderr);
        }

        public static int RunProcess(string exe, string args, int timeoutMs, out string stdout, out string stderr)
        {
            var o = new StringBuilder();
            var err = new StringBuilder();
            int rc = 255;
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                        {
                            if (e.Data != null) lock (o) { o.AppendLine(e.Data); }
                        };
                        p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                        {
                            if (e.Data != null) lock (err) { err.AppendLine(e.Data); }
                        };
                        p.BeginOutputReadLine(); p.BeginErrorReadLine();
                        if (p.WaitForExit(timeoutMs))
                        {
                            // 带超时的等待返回后输出可能还没读完：再等一次，把两个流读到结尾
                            p.WaitForExit();
                            rc = p.ExitCode;
                        }
                        else { try { p.Kill(); } catch (Exception) { } }
                    }
                }
            }
            catch (Exception) { rc = 255; }
            lock (o) { stdout = o.ToString(); }
            lock (err) { stderr = err.ToString(); }
            return rc;
        }

        int GiveTimeoutMs { get { return (cfg.PeerTimeoutSec + 2) * 1000; } }

        public int RequestGivePeer()
        {
            string ip = ResolvePeer();
            string stdout;
            int rc = RunSsh(BuildArgs(cfg, "give peer", ip), GiveTimeoutMs, out stdout);
            if (rc != 1) return rc;
            // 退出码 1：对方是旧版本，不认识 peer。用旧写法重试一次（设计文档 5.3）。
            log.Info("relay: 'give peer' rejected (1), retrying once as 'give windows'");
            return RunSsh(BuildArgs(cfg, "give windows", ip), GiveTimeoutMs, out stdout);
        }

        public int RequestGiveCode(uint code)
        {
            if (code < 1 || code > 255)
            {
                log.Error("relay: rejected code out of range: " + code);
                return 1;
            }
            string stdout;
            return RunSsh(BuildArgs(cfg, "give code " + code, ResolvePeer()), GiveTimeoutMs, out stdout);
        }

        public int RequestTake()
        {
            var sw = Stopwatch.StartNew();
            string ip = ResolvePeer();
            string stdout;
            return RunSsh(BuildArgs(cfg, "take", ip), TakeSshTimeoutMs(cfg, sw.ElapsedMilliseconds), out stdout);
        }

        public PeerInfo RequestInfo()
        {
            string stdout;
            int rc = RunSsh(BuildArgs(cfg, "info", ResolvePeer()), GiveTimeoutMs, out stdout);
            if (rc != 0) return null;
            PeerInfo info = PeerInfo.Parse(stdout);
            if (info == null) log.Error("relay: request info answered 0 but no info line");
            return info;
        }

        string ResolvePeer()
        {
            PeerResolveResult resolved = peerAddress.Resolve(cfg.PeerHost, peerAddressCachePath);
            log.Info(FormatResolveLog(cfg.PeerHost, resolved));
            return resolved.Ip == null ? null : resolved.Ip.ToString();
        }

        int RunSsh(string args, int timeoutMs, out string stdout)
        {
            string stderr;
            int rc = runner(args, timeoutMs, out stdout, out stderr);
            log.Info("relay: ssh " + args + " -> " + rc);
            if (rc != 0 && !string.IsNullOrEmpty(stderr))
            {
                string s = stderr.Trim();
                if (s.Length > 200) s = s.Substring(0, 200);
                log.Error("relay stderr: " + s);
            }
            return rc;
        }

        static string FormatResolveLog(string host, PeerResolveResult r)
        {
            if (r.Ip == null) return "relay: " + host + " -> (unresolved)";
            if (r.FromCache) return "relay: " + host + " -> " + r.Ip + " (last known, resolve failed: " + r.Error + ")";
            return "relay: " + host + " -> " + r.Ip + " (resolved, " + r.ElapsedMs + " ms)";
        }
    }
}
