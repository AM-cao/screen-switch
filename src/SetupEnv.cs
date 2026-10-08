using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScreenSwitch
{
    // 设置问答的真实环境：显示器、setup-client.ps1、SSH、托盘程序。dir 是程序所在的目录。
    public class SetupEnv : ISetupEnv
    {
        readonly string dir;
        readonly Log log;
        List<SetupMonitor> monitors;
        int connected;

        public SetupEnv(string dir)
        {
            this.dir = dir;
            log = new Log(Path.Combine(dir, "screenswitch.log"));
        }

        // 已连接的显示器以 CCD 的全部路径为准（含没点亮的，与布局模块看到的一致）；
        // 其中点亮的逐块读 VCP 0x60。CCD 查询失败时只列点亮的。
        public List<SetupMonitor> Monitors()
        {
            if (monitors != null) return monitors;
            List<MonitorProbe> probes = DxvaDdc.ProbeAll();
            string[] paths = null, names = null;
            try { CcdLayout.ConnectedTargets(out paths, out names); }
            catch (LayoutException e) { log.Error("setup: connected targets: " + e.Message); paths = null; }

            var list = new List<SetupMonitor>();
            var used = new List<MonitorProbe>();
            if (paths != null)
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    MonitorProbe hit = null;
                    foreach (MonitorProbe p in probes)
                        if (paths[i].Length > 0 && string.Equals(p.DeviceId, paths[i], StringComparison.OrdinalIgnoreCase)) hit = p;
                    if (hit != null) used.Add(hit);
                    list.Add(new SetupMonitor(paths[i], names[i], hit != null && hit.DdcOk, hit != null ? hit.Input : 0));
                }
            }
            // 点亮着、CCD 却没列出的（查询失败时是全部）：补在后面，名字没有就留空
            foreach (MonitorProbe p in probes)
                if (!used.Contains(p)) list.Add(new SetupMonitor(p.DeviceId, "", p.DdcOk, p.Input));
            // 块数以 CCD 为准；查询失败时退回点亮的块数
            connected = paths != null ? paths.Length : list.Count;
            foreach (SetupMonitor m in list)
                log.Info("setup: monitor " + m.DeviceId + " name=" + m.Name + " ddc=" + m.Ddc + " input=0x" + m.Input.ToString("X"));
            monitors = list;
            return list;
        }

        public int ConnectedCount()
        {
            Monitors();
            return connected;
        }

        public List<uint> InputCodes(string mainEdid)
        {
            string caps = new DxvaDdc(mainEdid, log).ReadCapabilities();
            if (caps == null)
            {
                log.Info("setup: capabilities not readable");
                return null;
            }
            log.Info("setup: capabilities " + caps);
            return DxvaDdc.ParseInputCodes(caps);
        }

        public int SshClientSetup(string user, string host, out string output)
        {
            output = "";
            string script = Path.Combine(dir, "setup-client.ps1");
            if (!File.Exists(script)) return -1;
            // -File：后面的参数按字面传给脚本，不会被当作表达式执行。-FromSetup 让脚本用 UTF-8 输出、不改 config.json。
            string args = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -PeerUser \"" + user + "\" -PeerHost \"" + host + "\" -FromSetup";
            string exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe");
            int rc = RunUtf8(File.Exists(exe) ? exe : "powershell.exe", args, 60000, out output);
            log.Info("setup: setup-client.ps1 " + user + "@" + host + " -> " + rc);
            return rc == -1 ? 255 : rc;
        }

        // 运行一个没有窗口的子进程，标准输出和标准错误按 UTF-8 合在一起读回来。启动不了、超时返回 255。
        static int RunUtf8(string exe, string args, int timeoutMs, out string output)
        {
            var sb = new StringBuilder();
            int rc = 255;
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
                using (var p = Process.Start(psi))
                {
                    DataReceivedEventHandler collect = delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) lock (sb) { sb.AppendLine(e.Data); }
                    };
                    p.OutputDataReceived += collect;
                    p.ErrorDataReceived += collect;
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (p.WaitForExit(timeoutMs))
                    {
                        p.WaitForExit();
                        rc = p.ExitCode;
                    }
                    else { try { p.Kill(); } catch (Exception) { } }
                }
            }
            catch (Exception) { rc = 255; }
            lock (sb) { output = sb.ToString(); }
            return rc;
        }

        public string PublicKey()
        {
            try
            {
                string home = Environment.GetEnvironmentVariable("USERPROFILE");
                if (string.IsNullOrEmpty(home)) return null;
                string path = Path.Combine(home, ".ssh\\id_ed25519.pub");
                if (!File.Exists(path)) return null;
                foreach (string line in File.ReadAllLines(path))
                    if (line.Trim().Length > 0) return line.Trim();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        // 占位，Task 5 换成真实现
        public string GenerateKey() { return PublicKey(); }
        public int AuthorizePeerKey(string safeKey, bool elevate, out string output) { output = ""; return 2; }
        public bool IsAdmin() { return false; }

        public PeerInfo RequestInfo(Config cfg)
        {
            return new SshRelay(cfg, log, dir).RequestInfo();
        }

        // 只动与本程序同一目录的 ScreenSwitch.exe（比较完整路径）：在运行就结束后重新启动，没在运行就启动。
        public TrayRestart RestartTray()
        {
            string exe = Path.GetFullPath(Path.Combine(dir, "ScreenSwitch.exe"));
            if (!File.Exists(exe)) return TrayRestart.Missing;
            bool wasRunning = false, stuck = false;
            foreach (Process p in Process.GetProcessesByName("ScreenSwitch"))
            {
                try
                {
                    string path;
                    // 别的会话或权限更高的进程读不到路径：不是我们的，不动
                    try { path = Path.GetFullPath(p.MainModule.FileName); }
                    catch (Exception) { continue; }
                    if (!string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    wasRunning = true;
                    try
                    {
                        p.Kill();
                        if (!p.WaitForExit(5000)) stuck = true;
                    }
                    catch (Exception) { stuck = !p.HasExited; }
                }
                catch (Exception) { stuck = true; }
                finally { p.Dispose(); }
            }
            if (stuck)
            {
                log.Error("setup: could not stop the running tray app");
                return TrayRestart.Failed;
            }
            try
            {
                // 经 shell 启动：不让托盘程序继承本进程的标准输出（否则管道另一头一直等不到结束）
                var psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = dir; psi.UseShellExecute = true;
                using (Process started = Process.Start(psi)) { }
            }
            catch (Exception e)
            {
                log.Error("setup: could not start the tray app: " + e.Message);
                return TrayRestart.Failed;
            }
            log.Info("setup: tray app " + (wasRunning ? "restarted" : "started"));
            return wasRunning ? TrayRestart.Restarted : TrayRestart.Started;
        }
    }
}
