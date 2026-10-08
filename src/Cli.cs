using System;

namespace ScreenSwitch
{
    public static class Cli
    {
        static Log OpenLog(string dir) { return new Log(System.IO.Path.Combine(dir, "screenswitch.log")); }
        static Config LoadConfig(string dir) { return Config.Load(System.IO.Path.Combine(dir, "config.json")); }

        // 输入口编码：十进制 1–255，只含数字
        public static bool ParseCode(string s, out uint code)
        {
            code = 0;
            if (string.IsNullOrEmpty(s) || s.Length > 3) return false;
            foreach (char ch in s) if (ch < '0' || ch > '9') return false;
            code = uint.Parse(s);
            return code >= 1 && code <= 255;
        }

        // 把 request give 的目标换成命令文件的内容（设计文档 5.1、5.3）：
        //   peer（旧写法 mac）→ give peer；self（旧写法 windows，只在本机用）→ give self；
        //   code <n> → give code <n>，n 要在本机的设备列表里；旧写法 other → 设备列表里的第一个设备。
        // 不接受时返回 null，error 为要回答的原因。
        public static string GiveCommand(Config cfg, string[] target, out string error)
        {
            error = "bad target";
            if (target.Length == 1)
            {
                if (target[0] == "peer" || target[0] == "mac") return "give peer";
                if (target[0] == "self" || target[0] == "windows") return "give self";
                if (target[0] == "other")
                {
                    if (cfg.Devices.Count > 0) return "give code " + cfg.Devices[0].Code;
                    error = "unknown device";
                }
                return null;
            }
            uint code;
            if (target.Length != 2 || target[0] != "code" || !ParseCode(target[1], out code)) return null;
            if (cfg.FindDevice(code) != null) return "give code " + code;
            error = "unknown device";
            return null;
        }

        // request give：0 accepted，2 not owned，3 daemon not running，1 目标不对或设备不在列表里。
        // 回答记进日志：对端经 ssh-gate 调用，托盘程序只在写了命令文件时才看得到。
        public static int RequestGive(string dir, Config cfg, string[] target, long now)
        {
            Log log = OpenLog(dir);
            string what = "request give " + string.Join(" ", target);
            string error;
            string cmd = GiveCommand(cfg, target, out error);
            if (cmd == null) return Answer(log, what, error, 1);
            bool owned; long ts;
            if (!StateFile.ReadState(dir, out owned, out ts) || now - ts > 10) return Answer(log, what, "daemon not running", 3);
            if (!owned) return Answer(log, what, "not owned", 2);
            StateFile.WriteCommand(dir, cmd);
            return Answer(log, what, "accepted", 0);
        }

        static int Answer(Log log, string what, string text, int rc)
        {
            log.Info(what + ": " + text);
            Console.WriteLine(text);
            return rc;
        }

        // 设计文档 5.2 request take：对端即将把主屏切给本机。未占有时写 pre-take，由托盘程序预先恢复布局；
        // 已占有时不动。0 已接受或已占有，3 托盘程序未运行。
        // 回答记进日志：对端经 ssh-gate 调用，托盘程序只在写了 pre-take 时才看得到。
        public static int RequestTake(string dir, long now)
        {
            Log log = OpenLog(dir);
            bool owned; long ts;
            if (!StateFile.ReadState(dir, out owned, out ts) || now - ts > 10)
            {
                log.Info("request take: daemon not running");
                Console.WriteLine("daemon not running");
                return 3;
            }
            if (owned)
            {
                log.Info("request take: already owned");
                Console.WriteLine("already owned");
                return 0;
            }
            StateFile.WriteCommand(dir, "pre-take");
            log.Info("request take: accepted, pre-take written");
            Console.WriteLine("accepted");
            return 0;
        }

        // request info（设计文档 5.4）：回答本机的配置，托盘程序没在运行也回答。
        public static int RequestInfo(Config cfg)
        {
            Console.WriteLine(PeerInfo.Format(cfg));
            return 0;
        }

        // 对方 SSH 公钥的强制命令入口（设计文档 5.3）。只放行：
        //   <…ScreenSwitch(Cli).exe> request give peer | give code <n> | take | info
        // 以及旧写法 give mac、give other。give self / give windows 只在本机用，不放行；带多余参数一律拒绝。
        // 程序路径可以含空格或带引号：最后一个 request 之前的部分都算程序路径（它只用来核对名字，不会被执行）。
        public static int SshGate(string original, string dir, long now)
        {
            string[] parts = original == null
                ? new string[0]
                : original.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int r = Array.LastIndexOf(parts, "request");
            bool ok = r >= 1;
            if (ok)
            {
                string exe = string.Join(" ", parts, 0, r).Trim('"').ToLowerInvariant();
                ok = exe.EndsWith("screenswitch.exe") || exe.EndsWith("screenswitchcli.exe");
            }
            string[] a = new string[ok ? parts.Length - r - 1 : 0];
            if (ok) Array.Copy(parts, r + 1, a, 0, a.Length);
            uint code;
            bool take = a.Length == 1 && a[0] == "take";
            bool info = a.Length == 1 && a[0] == "info";
            bool give = (a.Length == 2 && a[0] == "give" && (a[1] == "peer" || a[1] == "mac" || a[1] == "other"))
                || (a.Length == 3 && a[0] == "give" && a[1] == "code" && ParseCode(a[2], out code));
            if (!take && !info && !give)
            {
                OpenLog(dir).Error("ssh-gate rejected: " + (original ?? "<null>"));
                Console.WriteLine("rejected");
                return 1;
            }
            if (take) return RequestTake(dir, now);
            if (info) return RequestInfo(LoadConfig(dir));
            string[] target = new string[a.Length - 1];
            Array.Copy(a, 1, target, 0, target.Length);
            return RequestGive(dir, LoadConfig(dir), target, now);
        }

        public static int Run(string[] args, string dir)
        {
            if (args.Length == 0) return 1;
            switch (args[0])
            {
                case "request":
                    {
                        if (args.Length == 2 && args[1] == "take") return RequestTake(dir, StateFile.Now());
                        if (args.Length == 2 && args[1] == "info") return RequestInfo(LoadConfig(dir));
                        if (args.Length < 3 || args[1] != "give") return 1;
                        string[] target = new string[args.Length - 2];
                        Array.Copy(args, 2, target, 0, target.Length);
                        return RequestGive(dir, LoadConfig(dir), target, StateFile.Now());
                    }
                case "ssh-gate":
                    return SshGate(Environment.GetEnvironmentVariable("SSH_ORIGINAL_COMMAND"), dir, StateFile.Now());
                case "setup":
                    {
                        // 设置问答（设计文档第 9 节）：setup 走全部步骤，setup devices 只改设备列表
                        string[] rest = new string[args.Length - 1];
                        Array.Copy(args, 1, rest, 0, rest.Length);
                        return Setup.Run(rest, System.IO.Path.Combine(dir, "config.json"), ConsoleIo.In(), ConsoleIo.Out(),
                            new SetupEnv(dir), !ConsoleIo.InputIsConsole);
                    }
                case "icon-dump":
                    {
                        if (args.Length != 2) return 1;
                        System.IO.Directory.CreateDirectory(args[1]);
                        foreach (int sz in new int[] { 16, 24, 32, 64 })
                        {
                            using (var b = TrayIcons.Draw(true, sz)) b.Save(System.IO.Path.Combine(args[1], "owned-" + sz + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                            using (var b = TrayIcons.Draw(false, sz)) b.Save(System.IO.Path.Combine(args[1], "away-" + sz + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Console.WriteLine("icons written to " + args[1]);
                        return 0;
                    }
                case "diag":
                    {
                        var cfg = Config.Load(Paths.Config);
                        var ddc = new DxvaDdc(cfg.MainEdid, new Log(Paths.Log));
                        Console.Write(ddc.Describe());
                        return 0;
                    }
                case "ddc-read":
                    {
                        var cfg = Config.Load(Paths.Config);
                        var ddc = new DxvaDdc(cfg.MainEdid, new Log(Paths.Log));
                        uint v; bool ok = ddc.TryReadInput(out v);
                        Console.WriteLine("ok=" + ok + " value=0x" + v.ToString("X"));
                        return ok ? 0 : 1;
                    }
                case "ddc-write":
                    {
                        if (args.Length != 2) return 1;
                        var cfg = Config.Load(Paths.Config);
                        var ddc = new DxvaDdc(cfg.MainEdid, new Log(Paths.Log));
                        uint v;
                        try
                        {
                            v = Convert.ToUInt32(args[1], 16);
                        }
                        catch (FormatException)
                        {
                            Console.WriteLine("bad hex value: " + args[1]);
                            return 1;
                        }
                        catch (OverflowException)
                        {
                            Console.WriteLine("bad hex value: " + args[1]);
                            return 1;
                        }
                        catch (ArgumentException)
                        {
                            Console.WriteLine("bad hex value: " + args[1]);
                            return 1;
                        }
                        bool ok = ddc.WriteInput(v);
                        Console.WriteLine("write 0x" + v.ToString("X") + " ok=" + ok);
                        return ok ? 0 : 1;
                    }
                case "layout-show":
                case "layout-save":
                case "layout-take":
                case "layout-take-main":
                case "layout-release":
                    {
                        var cfg = Config.Load(Paths.Config);
                        var layout = new CcdLayout(Paths.Snapshot, cfg, new Log(Paths.Log));
                        try
                        {
                            if (args[0] == "layout-save") layout.SaveSnapshot();
                            else if (args[0] == "layout-take") layout.Take(OwnedLayout.Extend);
                            else if (args[0] == "layout-take-main") layout.Take(OwnedLayout.MainOnly);
                            else if (args[0] == "layout-release") layout.Release();
                            Console.Write(layout.Show());
                            return 0;
                        }
                        catch (LayoutException e) { Console.WriteLine("ERROR: " + e.Message); return 1; }
                    }
                case "layout-mode":
                    {
                        var cfg = Config.Load(Paths.Config);
                        var layout = new CcdLayout(Paths.Snapshot, cfg, new Log(Paths.Log));
                        string cur = layout.CurrentSignature();
                        string snap = layout.SnapshotSignature();
                        Console.WriteLine("mode=" + layout.Mode);
                        Console.WriteLine("current=" + (cur ?? "(none)"));
                        Console.WriteLine("snapshot=" + (snap ?? "(none)"));
                        return 0;
                    }
                default:
                    Console.WriteLine(Usage);
                    return 1;
            }
        }

        const string Usage =
            "usage: ScreenSwitchCli.exe <command>\r\n" +
            "  setup                             interactive setup (questions in Chinese): main screen, this computer, the other\r\n" +
            "                                    computer, devices; writes config.json and restarts the tray app\r\n" +
            "  setup devices                     only the device list: rename, delete, add\r\n" +
            "  request give peer                 ask the running tray app to switch the main screen to the other computer\r\n" +
            "  request give code <n>             ... to the device on input code n (decimal, must be in the device list)\r\n" +
            "  request give self                 ... or to re-apply this computer's layout\r\n" +
            "                                    (exit 0 accepted, 2 not owned, 3 not running, 1 bad target / unknown device)\r\n" +
            "  request take                      the other computer is about to switch here: restore the layout now (exit 0 ok, 3 not running)\r\n" +
            "  request info                      print this computer's code, name and devices (one line, names in Base64)\r\n" +
            "  diag                              list monitors and check DDC/CI on the main monitor\r\n" +
            "  ddc-read                          read the main monitor's input source (VCP 0x60)\r\n" +
            "  ddc-write <hex>                   switch the main monitor's input source\r\n" +
            "  layout-show                       active display paths and current mode\r\n" +
            "  layout-mode                       current mode and arrangement signatures (read-only)\r\n" +
            "  layout-save                       save the current extended layout as the snapshot\r\n" +
            "  layout-take                       apply the snapshot (extended, both screens)\r\n" +
            "  layout-take-main                  main screen only (secondary off)\r\n" +
            "  layout-release                    clone the secondary's desktop onto both screens\r\n" +
            "  icon-dump <dir>                   write the tray icons as PNG files";
    }
}
