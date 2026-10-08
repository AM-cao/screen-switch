using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScreenSwitch
{
    // 设置问答看到的一块已连接的显示器
    public class SetupMonitor
    {
        // 设备路径，例如 \\?\DISPLAY#LHC9059#5&26941f8e&0&UID4357#{…}
        public string DeviceId = "";
        // 给用户看的名字（EDID 里的型号名），可以为空
        public string Name = "";
        // 读 VCP 0x60 成功
        public bool Ddc;
        public uint Input;
        public SetupMonitor() { }
        public SetupMonitor(string deviceId, string name, bool ddc, uint input) { DeviceId = deviceId; Name = name; Ddc = ddc; Input = input; }
    }

    public enum TrayRestart { Restarted, Started, Missing, Failed }

    // 设置问答用到的硬件、SSH、托盘程序。真的实现是 SetupEnv，测试里换成假的。
    public interface ISetupEnv
    {
        // 已连接的显示器，每块是否应答 DDC/CI（读 VCP 0x60）
        List<SetupMonitor> Monitors();
        // 已连接的显示器数，含没点亮的
        int ConnectedCount();
        // 主屏 capabilities 里 VCP 0x60 支持的取值；读不到返回 null
        List<uint> InputCodes(string mainEdid);
        // 做本机的 SSH 客户端配置（setup-client.ps1）：0 成功，-1 脚本不在程序旁边（没有运行），其余为失败。
        // output 是脚本的输出。
        int SshClientSetup(string user, string host, out string output);
        // 本机 SSH 公钥那一行；没有返回 null
        string PublicKey();
        // 按 cfg 里的对方设置发 request info；取不到返回 null
        PeerInfo RequestInfo(Config cfg);
        // 重启与本程序同一目录的托盘程序
        TrayRestart RestartTray();
        // 生成 ~/.ssh/id_ed25519，返回公钥那一行；失败返回 null
        string GenerateKey();
        // 把对方的公钥放行到本机（运行程序旁边的 setup-sshd.ps1）：0 成功；2 需要管理员而 elevate 为 false；
        // 1223 用户在系统确认框里点了"否"；-1 setup-sshd.ps1 不在程序旁边；其余失败。output 是脚本的输出（弹确认框时为日志内容）
        int AuthorizePeerKey(string safeKey, bool elevate, out string output);
        // 当前进程是否以管理员身份运行
        bool IsAdmin();
    }

    // 设置问答（设计文档第 9 节）：ScreenSwitchCli.exe setup / setup devices。
    // 从现有配置出发，只改问到的项，最后一次写出；中途停下或输入结束都不写。
    public class Setup
    {
        // 输入结束（Ctrl+Z、管道读完）
        class Aborted : Exception { }
        // 无法继续：说明原因后退出码 1
        class Stopped : Exception
        {
            public Stopped(string m) : base(m) { }
        }

        const string DirectTakeRecord = "direct-take.txt";

        readonly string configPath;
        readonly TextReader input;
        readonly TextWriter output;
        readonly ISetupEnv env;
        // 输入不是键盘（管道、文件）时把读到的每一行也写出来，留下的记录才看得懂
        readonly bool echoInput;
        bool brokenConfig;

        Setup(string configPath, TextReader input, TextWriter output, ISetupEnv env, bool echoInput)
        {
            this.configPath = configPath; this.input = input; this.output = output; this.env = env; this.echoInput = echoInput;
        }

        public static int Run(string[] args, string configPath, TextReader input, TextWriter output, ISetupEnv env)
        {
            return Run(args, configPath, input, output, env, false);
        }

        // args：setup 后面的参数。没有 = 全部步骤；devices = 只改设备列表。
        public static int Run(string[] args, string configPath, TextReader input, TextWriter output, ISetupEnv env, bool echoInput)
        {
            bool devicesOnly = args.Length == 1 && args[0] == "devices";
            if (args.Length != 0 && !devicesOnly)
            {
                output.WriteLine(Tr.T("setup.usage.all"));
                output.WriteLine(Tr.T("setup.usage.devices"));
                return 1;
            }
            return new Setup(configPath, input, output, env, echoInput).Go(devicesOnly);
        }

        int Go(bool devicesOnly)
        {
            try
            {
                Config cfg = Config.Load(configPath);
                brokenConfig = cfg.LoadError != null;
                if (devicesOnly) DevicesOnly(cfg);
                else
                {
                    if (brokenConfig) Say(Tr.T("setup.config-unreadable", cfg.LoadError));
                    Full(cfg);
                }
                return Finish(cfg, !devicesOnly);
            }
            catch (Aborted)
            {
                Say("");
                Say(Tr.T("setup.input-ended"));
                return 1;
            }
            catch (Stopped e)
            {
                Say(e.Message);
                Say(Tr.T("setup.unchanged"));
                return 1;
            }
        }

        // ---- 纯函数 ----

        // 设备路径里的型号标识：\\?\DISPLAY#LHC9059#5&26941f8e&0&UID4357#{…} → LHC9059。认不出返回空串。
        public static string ModelId(string deviceId)
        {
            if (deviceId == null) return "";
            string[] p = deviceId.Split('#');
            if (p.Length >= 3) return p[1];
            p = deviceId.Split('\\');
            return p.Length >= 3 && string.Equals(p[0], "MONITOR", StringComparison.OrdinalIgnoreCase) ? p[1] : "";
        }

        // 型号加实例：LHC9059#5&26941f8e&0&UID4357。认不出返回空串。
        public static string InstanceId(string deviceId)
        {
            if (deviceId == null) return "";
            string[] p = deviceId.Split('#');
            return p.Length >= 3 && p[1].Length > 0 && p[2].Length > 0 ? p[1] + "#" + p[2] : "";
        }

        // 写进配置的主屏标识（其余代码把它当作设备路径的子串，不分大小写）：平时用型号；
        // 已连接的显示器里有同型号的，用"型号#实例"；还分不开（一个实例是另一个的前缀）再带上后面的 #。
        public static string MainEdidFor(string deviceId, IList<string> connected)
        {
            string model = ModelId(deviceId);
            if (model.Length == 0) return deviceId ?? "";
            int same = 0;
            foreach (string other in connected)
                if (string.Equals(ModelId(other), model, StringComparison.OrdinalIgnoreCase)) same++;
            if (same <= 1) return model;
            string inst = InstanceId(deviceId);
            if (inst.Length == 0) return deviceId;
            foreach (string other in connected)
            {
                if (other == null || string.Equals(other, deviceId, StringComparison.OrdinalIgnoreCase)) continue;
                if (other.IndexOf(inst, StringComparison.OrdinalIgnoreCase) >= 0) return inst + "#";
            }
            return inst;
        }

        // VCP 0x60 取值的标准含义；其余返回空串
        public static string InputName(uint code)
        {
            switch (code)
            {
                case 15: return "DisplayPort 1";
                case 16: return "DisplayPort 2";
                case 17: return "HDMI 1";
                case 18: return "HDMI 2";
                default: return "";
            }
        }

        // 公钥会被放进双引号交给对方的 shell：只留"类型 密钥 注释"，注释里只留字母、数字和 @._+-。
        // 不像公钥的返回 null。
        public static string SafeKey(string key)
        {
            if (key == null) return null;
            string[] f = key.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 2 || !Only(f[0], "@._+-") || !Only(f[1], "+/=")) return null;
            string s = f[0] + " " + f[1];
            if (f.Length < 3) return s;
            var comment = new StringBuilder();
            foreach (char ch in f[2]) if (Only(ch.ToString(), "@._+-")) comment.Append(ch);
            return comment.Length == 0 ? s : s + " " + comment;
        }

        // s 非空，且只含 ASCII 字母、数字和 extra 里的字符
        static bool Only(string s, string extra)
        {
            if (s.Length == 0) return false;
            foreach (char ch in s)
            {
                bool alnum = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
                if (!alnum && extra.IndexOf(ch) < 0) return false;
            }
            return true;
        }

        // 对方要执行的命令：把本机公钥授权为只能调用它的 ssh-gate
        public static string PeerCommand(string os, string key)
        {
            if (os == "windows") return "powershell -ExecutionPolicy Bypass -File setup-sshd.ps1 -PeerPubKey \"" + key + "\"";
            return "~/screenctl/screenctl.sh authorize \"" + key + "\"";
        }

        // ---- 全部步骤 ----

        static string NoDdc { get { return Tr.T("setup.no-ddc").Replace("\n", "\r\n"); } }

        void Full(Config cfg)
        {
            Say(Tr.T("setup.intro"));
            Say("");

            // 1 主屏
            List<SetupMonitor> mons = env.Monitors();
            var ddc = new List<SetupMonitor>();
            var ids = new List<string>();
            Say(Tr.T("setup.monitors-head"));
            for (int i = 0; i < mons.Count; i++)
            {
                SetupMonitor m = mons[i];
                ids.Add(m.DeviceId);
                if (m.Ddc) ddc.Add(m);
                Say(m.Ddc ? Tr.T("setup.mon-ddc", i + 1, Label(m), m.Input & 0xFF) : Tr.T("setup.mon-noddc", i + 1, Label(m)));
            }
            if (mons.Count == 0) Say(Tr.T("setup.none-found"));
            if (ddc.Count == 0) throw new Stopped(NoDdc);

            // 4 屏幕组合（不用提问，先查：不支持就不必往下问了）
            int count = Math.Max(env.ConnectedCount(), 1);
            if (count > 2) throw new Stopped(Tr.T("setup.too-many", count));

            SetupMonitor main;
            if (ddc.Count == 1)
            {
                main = ddc[0];
                // 已设置的主屏接着却没有应答，应答的是另一块：多半是主屏正显示别的设备（只认当前输入口的显示器
                // 这时不应答），不能悄悄把另一块屏当成主屏
                SetupMonitor configured = null;
                foreach (SetupMonitor m in mons)
                    if (Matches(m, cfg.MainEdid) && MatchCount(mons, cfg.MainEdid) == 1) configured = m;
                if (configured != null && configured != main)
                {
                    Say(Tr.T("setup.main-moved", Label(configured), Label(main)));
                    string a = Ask(Tr.T("setup.ask-swap-main", Label(main))).Trim().ToLowerInvariant();
                    if (a != "y" && a != "yes") throw new Stopped(Tr.T("setup.switch-main-first"));
                }
                Say(Tr.T("setup.main-auto", Label(main)));
                if (mons.Count > 1 && configured == null) Say(Tr.T("setup.main-maybe-other"));
            }
            else
            {
                main = ChooseMain(cfg, mons);
                Say(Tr.T("setup.main-line", Label(main)));
            }
            // 已配置的标识正好只认得出这一块时不改
            if (!(Matches(main, cfg.MainEdid) && MatchCount(mons, cfg.MainEdid) == 1)) cfg.MainEdid = MainEdidFor(main.DeviceId, ids);

            // 2 本机编码：主屏正显示本机，读到的就是本机所接的输入口
            uint code = main.Input & 0xFF;
            cfg.SelfCode = code;
            Say(code == 0 ? Tr.T("setup.self-code-zero") : Tr.T("setup.self-code", CodeLabel(code)));
            if (code != 0 && cfg.PeerCode == code)
            {
                cfg.PeerCode = 0;
                Say(Tr.T("setup.peer-code-cleared", code));
            }
            cfg.Layout = count >= 2 ? "second" : "none";
            Say(Tr.T("setup.layout-line", LayoutText(count >= 2)));
            Say("");

            // 3 本机名字
            string name = Ask(Tr.T("setup.ask-self-name", cfg.SelfName)).Trim();
            if (name.Length > 0) cfg.SelfName = name;

            // 5 对方，6 设备
            List<Device> fromPeer = PeerStep(cfg);
            Say("");
            if (!Adopt(cfg, fromPeer)) EditDevices(cfg);
        }

        // 屏幕组合的文字：second = 主屏加另一块屏
        static string LayoutText(bool second) { return second ? Tr.T("setup.layout-second") : Tr.T("setup.layout-none"); }

        static string Label(SetupMonitor m)
        {
            string model = ModelId(m.DeviceId), name = (m.Name ?? "").Trim();
            if (name.Length > 0 && model.Length > 0) return Tr.T("setup.label-paren", name, model);
            if (name.Length > 0) return name;
            return model.Length > 0 ? model : Tr.T("setup.unknown-monitor");
        }

        static string CodeLabel(uint code)
        {
            string n = InputName(code);
            return n.Length > 0 ? Tr.T("setup.label-paren", code, n) : code.ToString(CultureInfo.InvariantCulture);
        }

        static bool Blank(string s) { return s == null || s.Trim().Length == 0; }

        static bool Matches(SetupMonitor m, string mainEdid)
        {
            return !Blank(mainEdid) && m.DeviceId != null && m.DeviceId.IndexOf(mainEdid, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static int MatchCount(List<SetupMonitor> mons, string mainEdid)
        {
            int n = 0;
            foreach (SetupMonitor m in mons) if (Matches(m, mainEdid)) n++;
            return n;
        }

        // 几块屏都应答 DDC：让用户选。已配置的主屏（只认得出一块时）是默认选项。
        SetupMonitor ChooseMain(Config cfg, List<SetupMonitor> mons)
        {
            int def = 0, hits = 0;
            for (int i = 0; i < mons.Count; i++)
                if (mons[i].Ddc && Matches(mons[i], cfg.MainEdid)) { def = i + 1; hits++; }
            if (hits != 1) def = 0;
            for (;;)
            {
                string a = Ask(Tr.T("setup.choose-main", def > 0 ? Tr.T("setup.enter-default", def) : "")).Trim();
                int n;
                if (a.Length == 0 && def > 0) n = def;
                else if (!int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1 || n > mons.Count)
                {
                    Say(Tr.T("setup.bad-number"));
                    continue;
                }
                if (mons[n - 1].Ddc) return mons[n - 1];
                Say(Tr.T("setup.main-no-ddc"));
            }
        }

        // ---- 5 对方 ----

        // 返回从对方取到的设备列表；没有配置对方、没连上，返回 null
        List<Device> PeerStep(Config cfg)
        {
            Say("");
            if (cfg.PeerConfigured)
            {
                Say(Tr.T("setup.peer-line", cfg.PeerName, Config.DefaultPeerName(cfg.PeerOs), cfg.PeerUser, cfg.PeerHost));
                for (;;)
                {
                    string a = Ask(Tr.T("setup.ask-peer-keep")).Trim().ToLowerInvariant();
                    if (a.Length == 0) return AskPeerInfo(cfg);
                    if (a == "n") break;
                    if (a == "-")
                    {
                        cfg.PeerHost = ""; cfg.PeerUser = ""; cfg.PeerCode = 0;
                        Say(Tr.T("setup.peer-removed"));
                        return null;
                    }
                    Say(Tr.T("setup.peer-keep-bad"));
                }
            }
            else if (!YesNo(Tr.T("setup.ask-peer-now")))
            {
                Say(Tr.T("setup.peer-skipped"));
                return null;
            }

            bool had = cfg.PeerConfigured;
            bool defaultName = !had || cfg.PeerName == Config.DefaultPeerName(cfg.PeerOs);
            string name = Ask(defaultName ? Tr.T("setup.ask-peer-name-default") : Tr.T("setup.ask-peer-name", cfg.PeerName)).Trim();
            string os = AskOs(cfg.PeerOs);
            string host = AskHost(had ? cfg.PeerHost : "");
            string user = AskUser(had ? cfg.PeerUser : "");

            if (name.Length > 0) cfg.PeerName = name;
            else if (defaultName) cfg.PeerName = Config.DefaultPeerName(os);
            // 换了系统：对方的程序路径按新系统推导；换了主机：原来记的编码作废
            if (os != cfg.PeerOs) cfg.PeerProgram = "";
            if (!string.Equals(host, cfg.PeerHost, StringComparison.OrdinalIgnoreCase)) cfg.PeerCode = 0;
            cfg.PeerOs = os; cfg.PeerHost = host; cfg.PeerUser = user;

            Say("");
            Say(Tr.T("setup.ssh-config"));
            string text;
            int rc = env.SshClientSetup(user, host, out text);
            if (rc == -1)
            {
                Say(Tr.T("setup.ssh-script-missing"));
                Say("  powershell -ExecutionPolicy Bypass -File setup-client.ps1 -PeerUser " + user + " -PeerHost " + host);
            }
            else
            {
                foreach (string line in (text ?? "").Split('\n'))
                    if (line.Trim().Length > 0) Say("  " + line.TrimEnd());
                if (rc != 0) Say(Tr.T("setup.ssh-failed", rc));
            }

            string key = SafeKey(env.PublicKey());
            if (key == null)
            {
                Say(Tr.T("setup.key-missing", cfg.PeerName));
            }
            else
            {
                Say(Tr.T("setup.key-own"));
                Say("  " + key);
                Say(os == "windows"
                    ? Tr.T("setup.key-run-windows", cfg.PeerName)
                    : Tr.T("setup.key-run-mac", cfg.PeerName));
                Say("  " + PeerCommand(os, key));
                Ask(Tr.T("setup.key-continue"));
            }
            return AskPeerInfo(cfg);
        }

        // 能连通时用 request info 取对方的编码和设备列表；连不上不算错
        List<Device> AskPeerInfo(Config cfg)
        {
            Say(Tr.T("setup.asking-peer", cfg.PeerName));
            PeerInfo info = env.RequestInfo(cfg);
            if (info == null)
            {
                Say(cfg.PeerCode != 0
                    ? Tr.T("setup.peer-unreachable-keep", cfg.PeerName, cfg.PeerCode)
                    : Tr.T("setup.peer-unreachable", cfg.PeerName));
                return null;
            }
            if (info.Code == 0) Say(Tr.T("setup.peer-no-code", cfg.PeerName));
            else if (cfg.SelfCode != 0 && info.Code == cfg.SelfCode)
            {
                cfg.PeerCode = 0;
                Say(Tr.T("setup.peer-same-code", cfg.PeerName, info.Code));
            }
            else
            {
                cfg.PeerCode = info.Code;
                Say(Tr.T("setup.peer-code", cfg.PeerName, CodeLabel(info.Code)));
            }
            return info.Devices;
        }

        bool YesNo(string prompt)
        {
            for (;;)
            {
                string a = Ask(prompt).Trim().ToLowerInvariant();
                if (a.Length == 0 || a == "y" || a == "yes") return true;
                if (a == "n" || a == "no") return false;
                Say(Tr.T("setup.yes-no"));
            }
        }

        string AskOs(string current)
        {
            for (;;)
            {
                string a = Ask(Tr.T("setup.ask-os", current == "windows" ? "1" : "2")).Trim().ToLowerInvariant();
                if (a.Length == 0) return current == "windows" ? "windows" : "mac";
                if (a == "1" || a == "w" || a == "win" || a == "windows") return "windows";
                if (a == "2" || a == "m" || a == "mac" || a == "macos") return "mac";
                Say(Tr.T("setup.os-bad"));
            }
        }

        // 主机名和用户名会原样放进 ssh 的命令行和 ~/.ssh/config：只收不需要引号的写法
        public static bool ValidHost(string host)
        {
            return host != null && host.Length > 0 && Only(host, ".-_");
        }

        // Windows 的 whoami 输出"电脑名\用户名"：只要后半段。不合法返回 null。
        public static string CleanUser(string user)
        {
            if (user == null) return null;
            string a = user.Trim();
            a = a.Substring(a.LastIndexOf('\\') + 1);
            if (a.Length == 0) return null;
            foreach (char ch in a) if (!char.IsLetterOrDigit(ch) && "._-".IndexOf(ch) < 0) return null;
            return a;
        }

        string AskHost(string def)
        {
            for (;;)
            {
                string a = Ask(Tr.T("setup.ask-host", def.Length > 0 ? Tr.T("setup.or-enter", def) : "")).Trim();
                if (a.Length == 0 && def.Length > 0) return def;
                if (ValidHost(a)) return a;
                Say(Tr.T("setup.host-bad"));
            }
        }

        string AskUser(string def)
        {
            for (;;)
            {
                string a = Ask(Tr.T("setup.ask-user", def.Length > 0 ? Tr.T("setup.or-enter", def) : "")).Trim();
                if (a.Length == 0 && def.Length > 0) return def;
                string u = CleanUser(a);
                if (u != null) return u;
                Say(Tr.T("setup.user-bad"));
            }
        }

        // ---- 6 设备 ----

        // 从对方取到设备列表时直接采用：去掉本机和对方自己的编码，同一编码的设备原有的快捷键留着。
        // 对方没有设备（或去掉后一个不剩）返回 false，改为手工编辑。
        bool Adopt(Config cfg, List<Device> fromPeer)
        {
            if (fromPeer == null) return false;
            var list = new List<Device>();
            foreach (Device d in fromPeer)
            {
                if (d.Code < 1 || d.Code > 255 || d.Code == cfg.SelfCode || d.Code == cfg.PeerCode) continue;
                bool dup = false;
                foreach (Device x in list) if (x.Code == d.Code) dup = true;
                if (dup) continue;
                var nd = new Device(Blank(d.Name) ? Config.DefaultDeviceName(list.Count + 1) : d.Name.Trim(), d.Code);
                Device old = cfg.FindDevice(d.Code);
                if (old != null) nd.Hotkey = old.Hotkey;
                list.Add(nd);
            }
            if (list.Count == 0) return false;
            cfg.Devices = list;
            Say(Tr.T("setup.adopt-head", cfg.PeerName));
            foreach (Device d in list) Say("  " + d.Code + "  " + d.Name);
            Say(Tr.T("setup.adopt-hint"));
            return true;
        }

        void DevicesOnly(Config cfg)
        {
            if (Blank(cfg.MainEdid) || cfg.MainEdid == Config.Placeholder)
                throw new Stopped(Tr.T("setup.no-main-yet"));
            EditDevices(cfg);
        }

        // 逐个改名或删除已有的设备，再添加新的。已有设备的快捷键不动。
        void EditDevices(Config cfg)
        {
            Say(Tr.T("setup.devices-head"));
            ShowInputCodes(cfg);

            foreach (Device d in cfg.Devices.ToArray())
            {
                if (d.Code == cfg.SelfCode || d.Code == cfg.PeerCode)
                {
                    cfg.Devices.Remove(d);
                    Say(d.Code == cfg.SelfCode ? Tr.T("setup.dev-dropped-self", d.Name, d.Code) : Tr.T("setup.dev-dropped-peer", d.Name, d.Code, cfg.PeerName));
                    continue;
                }
                string a = Ask(Tr.T("setup.ask-dev-name", d.Name, d.Code)).Trim();
                if (a == "-")
                {
                    cfg.Devices.Remove(d);
                    Say(Tr.T("setup.dev-deleted", d.Name));
                }
                else if (a.Length > 0) d.Name = a;
            }

            for (;;)
            {
                string s = Ask(Tr.T("setup.ask-dev-code")).Trim();
                if (s.Length == 0) break;
                uint code;
                if (!Cli.ParseCode(s, out code))
                {
                    Say(Tr.T("setup.code-bad"));
                    continue;
                }
                if (code == cfg.SelfCode)
                {
                    Say(Tr.T("setup.code-is-self", code));
                    continue;
                }
                if (code == cfg.PeerCode)
                {
                    Say(Tr.T("setup.code-is-peer", code, cfg.PeerName));
                    continue;
                }
                Device taken = cfg.FindDevice(code);
                if (taken != null)
                {
                    Say(Tr.T("setup.code-taken", code, taken.Name));
                    continue;
                }
                string def = FreeDeviceName(cfg);
                string name = Ask(Tr.T("setup.ask-new-dev-name", def)).Trim();
                if (name == "-") continue;
                cfg.Devices.Add(new Device(name.Length > 0 ? name : def, code));
            }
        }

        static string FreeDeviceName(Config cfg)
        {
            for (int n = cfg.Devices.Count + 1; ; n++)
            {
                string name = Config.DefaultDeviceName(n);
                bool used = false;
                foreach (Device d in cfg.Devices) if (d.Name == name) used = true;
                if (!used) return name;
            }
        }

        // 列出主屏 capabilities 里 0x60 支持的取值和标准含义，去掉本机和对方已用的
        void ShowInputCodes(Config cfg)
        {
            Say(Tr.T("setup.reading-inputs"));
            List<uint> codes = env.InputCodes(cfg.MainEdid);
            if (codes == null)
            {
                Say(Tr.T("setup.inputs-unknown"));
                return;
            }
            var lines = new List<string>();
            foreach (uint code in codes)
            {
                if (code == cfg.SelfCode || code == cfg.PeerCode) continue;
                string line = "  " + code;
                if (InputName(code).Length > 0) line += "  " + InputName(code);
                Device d = cfg.FindDevice(code);
                if (d != null) line += Tr.T("setup.input-has-device", d.Name);
                lines.Add(line);
            }
            if (lines.Count == 0)
            {
                Say(Tr.T("setup.inputs-none"));
                return;
            }
            Say(Tr.T("setup.inputs-head"));
            foreach (string line in lines) Say(line);
        }

        // ---- 7 写配置、重启托盘程序 ----

        int Finish(Config cfg, bool full)
        {
            try
            {
                if (brokenConfig && File.Exists(configPath)) File.Copy(configPath, configPath + ".bak", true);
                cfg.Save(configPath);
            }
            catch (Exception e)
            {
                if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw;
                Say(Tr.T("setup.write-failed", configPath, e.Message));
                return 1;
            }
            // 重新设置过：清掉"直接切换从未成功"的记录（设计文档 6.3），让托盘程序重新试
            if (full)
            {
                try { File.Delete(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)), DirectTakeRecord)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Say("");
            Say(Tr.T("setup.written", configPath));
            switch (env.RestartTray())
            {
                case TrayRestart.Restarted: Say(Tr.T("setup.tray-restarted")); break;
                case TrayRestart.Started: Say(Tr.T("setup.tray-started")); break;
                case TrayRestart.Missing: Say(Tr.T("setup.tray-missing")); break;
                default: Say(Tr.T("setup.tray-failed")); break;
            }
            Say(Summary(cfg));
            return 0;
        }

        static string Summary(Config cfg)
        {
            string peer = cfg.PeerConfigured
                ? Tr.T("setup.summary-peer", cfg.PeerName, cfg.PeerCode == 0 ? Tr.T("setup.unknown") : cfg.PeerCode.ToString(CultureInfo.InvariantCulture))
                : Tr.T("setup.summary-no-peer");
            string devices = Tr.T("setup.summary-no-devices");
            if (cfg.Devices.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < cfg.Devices.Count; i++)
                    sb.Append(i > 0 ? Tr.T("list.sep") : "").Append(cfg.Devices[i].Name).Append("(").Append(cfg.Devices[i].Code).Append(")");
                devices = Tr.T("setup.summary-devices", sb.ToString());
            }
            return Tr.T("setup.summary", cfg.MainEdid, cfg.SelfName, cfg.SelfCode, LayoutText(!cfg.LayoutNone), peer, devices);
        }

        // ---- 读写 ----

        void Say(string line) { output.WriteLine(line); }

        string Ask(string prompt)
        {
            // 英文提示以半角冒号结尾：冒号后补一个空格（中文提示以全角冒号结尾，不受影响）
            if (prompt.EndsWith(":")) prompt += " ";
            output.Write(prompt);
            output.Flush();
            string s = input.ReadLine();
            if (s == null) throw new Aborted();
            if (echoInput) output.WriteLine(s);
            return s;
        }
    }
}
