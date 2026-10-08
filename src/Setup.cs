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
        // 生成本机的 SSH 密钥对，返回公钥那一行；失败返回 null
        string GenerateKey();
        // 把对方的公钥放行到本机（elevate 为真时以管理员身份做）：0 成功，其余为失败；output 是脚本的输出
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
                output.WriteLine("用法：ScreenSwitchCli.exe setup            全部设置");
                output.WriteLine("      ScreenSwitchCli.exe setup devices    只改设备列表");
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
                    if (brokenConfig) Say("现有的 config.json 读不了（" + cfg.LoadError + "），会重新生成，原文件另存为 config.json.bak。");
                    Full(cfg);
                }
                return Finish(cfg, !devicesOnly);
            }
            catch (Aborted)
            {
                Say("");
                Say("输入结束了，配置没有改动。");
                return 1;
            }
            catch (Stopped e)
            {
                Say(e.Message);
                Say("配置没有改动。");
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

        const string NoDdc =
            "没有显示器应答 DDC/CI，无法设置。可能的原因：\r\n" +
            "  - 显示器自己的菜单（OSD）里没有打开 DDC/CI；\r\n" +
            "  - 连接线、扩展坞或转接头不传 DDC；\r\n" +
            "  - 有的显示器只在当前输入口应答，而主屏现在显示的是别的设备：把主屏切到这台电脑后再运行一次。";

        void Full(Config cfg)
        {
            Say("ScreenSwitch 设置。开始前请确认：主屏（两台电脑共用的那块显示器）正显示这台电脑的画面。");
            Say("");

            // 1 主屏
            List<SetupMonitor> mons = env.Monitors();
            var ddc = new List<SetupMonitor>();
            var ids = new List<string>();
            Say("已连接的显示器：");
            for (int i = 0; i < mons.Count; i++)
            {
                SetupMonitor m = mons[i];
                ids.Add(m.DeviceId);
                if (m.Ddc) ddc.Add(m);
                Say("  " + (i + 1) + ". " + Label(m) + "：" + (m.Ddc ? "DDC/CI 有应答，当前输入口编码 " + (m.Input & 0xFF) : "DDC/CI 没有应答"));
            }
            if (mons.Count == 0) Say("  （没有找到）");
            if (ddc.Count == 0) throw new Stopped(NoDdc);

            // 4 屏幕组合（不用提问，先查：不支持就不必往下问了）
            int count = Math.Max(env.ConnectedCount(), 1);
            if (count > 2) throw new Stopped("这台电脑接了 " + count + " 块屏。三块及以上的屏不支持：只留主屏，或主屏加另一块屏，然后再运行一次。");

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
                    Say("原来设置的主屏是 " + Label(configured) + "，它现在没有应答 DDC/CI（多半是正显示别的设备）；应答的只有 " + Label(main) + "。");
                    string a = Ask("把主屏换成 " + Label(main) + " 吗？（y = 换，回车 = 不换）：").Trim().ToLowerInvariant();
                    if (a != "y" && a != "yes") throw new Stopped("请把主屏切到这台电脑（用显示器上的按钮），再运行一次。");
                }
                Say("主屏：" + Label(main) + "（只有它应答 DDC/CI，已自动选中）");
                if (mons.Count > 1 && configured == null) Say("如果主屏其实是另一块（它此刻正显示别的设备所以没有应答），请按 Ctrl+C 退出，把主屏切到这台电脑后再运行。");
            }
            else
            {
                main = ChooseMain(cfg, mons);
                Say("主屏：" + Label(main));
            }
            // 已配置的标识正好只认得出这一块时不改
            if (!(Matches(main, cfg.MainEdid) && MatchCount(mons, cfg.MainEdid) == 1)) cfg.MainEdid = MainEdidFor(main.DeviceId, ids);

            // 2 本机编码：主屏正显示本机，读到的就是本机所接的输入口
            uint code = main.Input & 0xFF;
            cfg.SelfCode = code;
            Say(code == 0 ? "本机编码：读到的是 0，记为未知。" : "本机编码：" + CodeLabel(code));
            if (code != 0 && cfg.PeerCode == code)
            {
                cfg.PeerCode = 0;
                Say("原来记的对方编码也是 " + code + "，已清除。");
            }
            cfg.Layout = count >= 2 ? "second" : "none";
            Say("屏幕组合：" + (count >= 2 ? "主屏加另一块屏" : "只有主屏"));
            Say("");

            // 3 本机名字
            string name = Ask("本机名字 [" + cfg.SelfName + "]（回车 = 不改）：").Trim();
            if (name.Length > 0) cfg.SelfName = name;

            // 5 对方，6 设备
            List<Device> fromPeer = PeerStep(cfg);
            Say("");
            if (!Adopt(cfg, fromPeer)) EditDevices(cfg);
        }

        static string Label(SetupMonitor m)
        {
            string model = ModelId(m.DeviceId), name = (m.Name ?? "").Trim();
            if (name.Length > 0 && model.Length > 0) return name + "（" + model + "）";
            if (name.Length > 0) return name;
            return model.Length > 0 ? model : "未知显示器";
        }

        static string CodeLabel(uint code)
        {
            string n = InputName(code);
            return n.Length > 0 ? code + "（" + n + "）" : code.ToString(CultureInfo.InvariantCulture);
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
                string a = Ask("有几块显示器应答 DDC/CI。主屏是哪一块？输入序号" + (def > 0 ? "（回车 = " + def + "）" : "") + "：").Trim();
                int n;
                if (a.Length == 0 && def > 0) n = def;
                else if (!int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1 || n > mons.Count)
                {
                    Say("请输入列表里的序号。");
                    continue;
                }
                if (mons[n - 1].Ddc) return mons[n - 1];
                Say("这块显示器没有应答 DDC/CI，不能作主屏。");
            }
        }

        // ---- 5 对方 ----

        // 返回从对方取到的设备列表；没有配置对方、没连上，返回 null
        List<Device> PeerStep(Config cfg)
        {
            Say("");
            if (cfg.PeerConfigured)
            {
                Say("另一台电脑：" + cfg.PeerName + "（" + Config.DefaultPeerName(cfg.PeerOs) + "，" + cfg.PeerUser + "@" + cfg.PeerHost + "）");
                for (;;)
                {
                    string a = Ask("回车 = 保留，n = 重新填写，- = 不要对方：").Trim().ToLowerInvariant();
                    if (a.Length == 0) return AskPeerInfo(cfg);
                    if (a == "n") break;
                    if (a == "-")
                    {
                        cfg.PeerHost = ""; cfg.PeerUser = ""; cfg.PeerCode = 0;
                        Say("已去掉对方：主屏不在本机时要用显示器上的按钮切回来。");
                        return null;
                    }
                    Say("请直接回车，或输入 n 或 -。");
                }
            }
            else if (!YesNo("现在设置另一台电脑吗？（y / n，回车 = y）："))
            {
                Say("已跳过。以后要设置，再运行一次 ScreenSwitchCli.exe setup。");
                return null;
            }

            bool had = cfg.PeerConfigured;
            bool defaultName = !had || cfg.PeerName == Config.DefaultPeerName(cfg.PeerOs);
            string name = Ask(defaultName ? "对方的名字（回车 = 按系统叫 Windows 或 Mac）：" : "对方的名字 [" + cfg.PeerName + "]：").Trim();
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
            Say("正在配置本机的 SSH（密钥和 ~/.ssh/config）…");
            string text;
            int rc = env.SshClientSetup(user, host, out text);
            if (rc == -1)
            {
                Say("程序旁边没有 setup-client.ps1，这一步没有做。请在仓库目录里手动运行：");
                Say("  powershell -ExecutionPolicy Bypass -File setup-client.ps1 -PeerUser " + user + " -PeerHost " + host);
            }
            else
            {
                foreach (string line in (text ?? "").Split('\n'))
                    if (line.Trim().Length > 0) Say("  " + line.TrimEnd());
                if (rc != 0) Say("SSH 配置没有成功（退出码 " + rc + "）。可以稍后重新运行设置。");
            }

            string key = SafeKey(env.PublicKey());
            if (key == null)
            {
                Say("没有找到本机的 SSH 公钥（~/.ssh/id_ed25519.pub），" + cfg.PeerName + " 暂时无法授权这台电脑。");
            }
            else
            {
                Say("本机的公钥：");
                Say("  " + key);
                Say(os == "windows"
                    ? "请在 " + cfg.PeerName + " 上打开管理员 PowerShell，进入 ScreenSwitch 的安装目录，执行："
                    : "请在 " + cfg.PeerName + " 的终端里执行：");
                Say("  " + PeerCommand(os, key));
                Ask("执行完后按回车继续（现在做不了也按回车）：");
            }
            return AskPeerInfo(cfg);
        }

        // 能连通时用 request info 取对方的编码和设备列表；连不上不算错
        List<Device> AskPeerInfo(Config cfg)
        {
            Say("正在询问 " + cfg.PeerName + " …");
            PeerInfo info = env.RequestInfo(cfg);
            if (info == null)
            {
                Say(cfg.PeerCode != 0
                    ? "没有连上 " + cfg.PeerName + "，沿用已有的编码 " + cfg.PeerCode + "。"
                    : "没有连上 " + cfg.PeerName + "：它接在哪个输入口还不知道。以后第一次切给它时，托盘程序会自动询问，不用再运行设置。");
                return null;
            }
            if (info.Code == 0) Say(cfg.PeerName + " 还不知道自己接在哪个输入口（它那边还没有运行设置）。");
            else if (cfg.SelfCode != 0 && info.Code == cfg.SelfCode)
            {
                cfg.PeerCode = 0;
                Say(cfg.PeerName + " 报告的编码和本机相同（" + info.Code + "）：其中一台运行设置时，主屏显示的不是它自己。请在那台电脑上重新运行设置。");
            }
            else
            {
                cfg.PeerCode = info.Code;
                Say(cfg.PeerName + " 的编码：" + CodeLabel(info.Code));
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
                Say("请输入 y 或 n。");
            }
        }

        string AskOs(string current)
        {
            for (;;)
            {
                string a = Ask("对方的系统（1 = Windows，2 = Mac，回车 = " + (current == "windows" ? "1" : "2") + "）：").Trim().ToLowerInvariant();
                if (a.Length == 0) return current == "windows" ? "windows" : "mac";
                if (a == "1" || a == "w" || a == "win" || a == "windows") return "windows";
                if (a == "2" || a == "m" || a == "mac" || a == "macos") return "mac";
                Say("请输入 1 或 2。");
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
                string a = Ask("对方的主机名或 IP（例如 my-mac.local 或 192.168.1.20" + (def.Length > 0 ? "，回车 = " + def : "") + "）：").Trim();
                if (a.Length == 0 && def.Length > 0) return def;
                if (ValidHost(a)) return a;
                Say("主机名不能为空，只能用字母、数字、点和连字符。");
            }
        }

        string AskUser(string def)
        {
            for (;;)
            {
                string a = Ask("对方的用户名（在对方电脑上运行 whoami 可以看到" + (def.Length > 0 ? "，回车 = " + def : "") + "）：").Trim();
                if (a.Length == 0 && def.Length > 0) return def;
                string u = CleanUser(a);
                if (u != null) return u;
                Say("用户名不能为空，也不能有空格、引号、@ 等符号。");
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
            Say("其他设备（采用 " + cfg.PeerName + " 上的设置）：");
            foreach (Device d in list) Say("  " + d.Code + "  " + d.Name);
            Say("要改名或增删，运行 ScreenSwitchCli.exe setup devices。");
            return true;
        }

        void DevicesOnly(Config cfg)
        {
            if (Blank(cfg.MainEdid) || cfg.MainEdid == Config.Placeholder)
                throw new Stopped("还没有设置主屏：请先运行 ScreenSwitchCli.exe setup。");
            EditDevices(cfg);
        }

        // 逐个改名或删除已有的设备，再添加新的。已有设备的快捷键不动。
        void EditDevices(Config cfg)
        {
            Say("其他设备（游戏机等，接在主屏别的输入口上）：");
            ShowInputCodes(cfg);

            foreach (Device d in cfg.Devices.ToArray())
            {
                if (d.Code == cfg.SelfCode || d.Code == cfg.PeerCode)
                {
                    cfg.Devices.Remove(d);
                    Say("设备“" + d.Name + "”的编码 " + d.Code + " 是" + (d.Code == cfg.SelfCode ? "本机" : cfg.PeerName) + "的，已去掉。");
                    continue;
                }
                string a = Ask("设备“" + d.Name + "”（编码 " + d.Code + "）的新名字（回车 = 不改，- = 删除）：").Trim();
                if (a == "-")
                {
                    cfg.Devices.Remove(d);
                    Say("已删除“" + d.Name + "”。");
                }
                else if (a.Length > 0) d.Name = a;
            }

            for (;;)
            {
                string s = Ask("要添加的设备的输入口编码（1–255，回车 = 结束）：").Trim();
                if (s.Length == 0) break;
                uint code;
                if (!Cli.ParseCode(s, out code))
                {
                    Say("编码要写十进制数字，1 到 255。");
                    continue;
                }
                if (code == cfg.SelfCode)
                {
                    Say(code + " 是本机的编码，不能再给设备用。");
                    continue;
                }
                if (code == cfg.PeerCode)
                {
                    Say(code + " 是 " + cfg.PeerName + " 的编码，不能再给设备用。");
                    continue;
                }
                Device taken = cfg.FindDevice(code);
                if (taken != null)
                {
                    Say("编码 " + code + " 已经是设备“" + taken.Name + "”。");
                    continue;
                }
                string def = FreeDeviceName(cfg);
                string name = Ask("它的名字（回车 = " + def + "，- = 不加了）：").Trim();
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
            Say("正在读取主屏支持的输入口（要几秒）…");
            List<uint> codes = env.InputCodes(cfg.MainEdid);
            if (codes == null)
            {
                Say("读不到主屏支持哪些输入口，请直接输入编码。常见的：15 = DisplayPort 1，16 = DisplayPort 2，17 = HDMI 1，18 = HDMI 2。");
                return;
            }
            var lines = new List<string>();
            foreach (uint code in codes)
            {
                if (code == cfg.SelfCode || code == cfg.PeerCode) continue;
                string line = "  " + code;
                if (InputName(code).Length > 0) line += "  " + InputName(code);
                Device d = cfg.FindDevice(code);
                if (d != null) line += "  （已有设备“" + d.Name + "”）";
                lines.Add(line);
            }
            if (lines.Count == 0)
            {
                Say("主屏没有报告别的输入口，请直接输入编码。");
                return;
            }
            Say("主屏支持的输入口编码（本机和对方的已去掉）：");
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
                Say("写不了 " + configPath + "：" + e.Message);
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
            Say("已写入 " + configPath);
            switch (env.RestartTray())
            {
                case TrayRestart.Restarted: Say("托盘程序已重启。"); break;
                case TrayRestart.Started: Say("托盘程序已启动。"); break;
                case TrayRestart.Missing: Say("程序旁边没有 ScreenSwitch.exe，没有重启托盘程序。"); break;
                default: Say("托盘程序没能重启：请退出后重新运行 ScreenSwitch.exe。"); break;
            }
            Say(Summary(cfg));
            return 0;
        }

        static string Summary(Config cfg)
        {
            var sb = new StringBuilder("设置完成：主屏 " + cfg.MainEdid + "，本机 " + cfg.SelfName + "（编码 " + cfg.SelfCode + "），");
            sb.Append(cfg.LayoutNone ? "只有主屏" : "主屏加另一块屏");
            sb.Append(cfg.PeerConfigured ? "，对方 " + cfg.PeerName + "（编码 " + (cfg.PeerCode == 0 ? "未知" : cfg.PeerCode.ToString(CultureInfo.InvariantCulture)) + "）" : "，没有配置对方");
            if (cfg.Devices.Count == 0) return sb.Append("，没有其他设备。").ToString();
            sb.Append("，设备 ");
            for (int i = 0; i < cfg.Devices.Count; i++)
                sb.Append(i > 0 ? "、" : "").Append(cfg.Devices[i].Name).Append("(").Append(cfg.Devices[i].Code).Append(")");
            return sb.Append("。").ToString();
        }

        // ---- 读写 ----

        void Say(string line) { output.WriteLine(line); }

        string Ask(string prompt)
        {
            output.Write(prompt);
            output.Flush();
            string s = input.ReadLine();
            if (s == null) throw new Aborted();
            if (echoInput) output.WriteLine(s);
            return s;
        }
    }
}
