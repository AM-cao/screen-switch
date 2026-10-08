using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ScreenSwitch
{
    // 接在主屏其他输入口上的设备（游戏机等）
    public class Device
    {
        public string Name = "";
        public uint Code;
        public string Hotkey = "";
        public Device() { }
        public Device(string name, uint code) { Name = name; Code = code; }
    }

    // 配置（设计文档 4.1）。旧的键（codes、peer.command、hotkeys）照常能读，Save 只写新键。
    public class Config
    {
        public const string Placeholder = "CHANGE_ME";

        public string MainEdid = Placeholder;
        // second = 另有一块屏；none = 只有主屏
        public string Layout = "second";
        public string SelfName = Environment.MachineName;
        // 本机所接输入口的编码；0 = 未知
        public uint SelfCode = 0;
        public string PeerName = "Mac";
        // mac | windows：只用于推导 PeerProgram 的默认值和默认名字
        public string PeerOs = "mac";
        public string PeerHost = "";
        public string PeerUser = "";
        // 对方所接输入口的编码；0 = 未知
        public uint PeerCode = 0;
        // 对方机器上要调用的程序；空 = 按 PeerOs 推导（见 PeerProgramPath）
        public string PeerProgram = "";
        public string PeerHotkey = "";
        public string PeerKeyPath = "";
        public int PeerTimeoutSec = 3;
        public List<Device> Devices = new List<Device>();
        public bool DirectTake = true;
        public int PollMs = 2000;
        public int Debounce = 2;
        public string SshExe = "ssh.exe";
        // 让出时克隆用的分辨率；0 = 取快照里另一块屏自己的分辨率
        public int ReleaseWidth = 0;
        public int ReleaseHeight = 0;
        // 拿回主屏（布局恢复成功）之后要执行的命令，空 = 不执行；delayMs 内的多次拿回只执行一次
        public string AfterTakeCommand = "";
        public int AfterTakeDelayMs = 3000;
        public string LoadError = null;

        public bool LayoutNone { get { return Layout == "none"; } }

        // peer.host 或 peer.user 为空（或还是占位符）= 没有配置对方，不发 SSH
        public bool PeerConfigured
        {
            get { return !Blank(PeerHost) && !Blank(PeerUser) && PeerHost != Placeholder && PeerUser != Placeholder; }
        }

        public string PeerProgramPath
        {
            get
            {
                if (!Blank(PeerProgram)) return PeerProgram;
                if (PeerOs == "windows") return "C:\\Users\\" + PeerUser + "\\ScreenSwitch\\ScreenSwitch.exe";
                return "~/screenctl/screenctl.sh";
            }
        }

        public Device FindDevice(uint code)
        {
            foreach (Device d in Devices) if (d.Code == code) return d;
            return null;
        }

        public static string DefaultPeerName(string os) { return os == "windows" ? "Windows" : "Mac"; }
        public static string DefaultDeviceName(int n) { return Tr.T("device.default", n); }

        static bool Blank(string s) { return s == null || s.Trim().Length == 0; }

        public static Config Load(string path)
        {
            var c = new Config();
            if (!File.Exists(path)) return c;
            try { Parse(c, File.ReadAllText(path)); }
            catch (Exception e)
            {
                var def = new Config();
                def.LoadError = e.Message;
                return def;
            }
            return c;
        }

        const string LegacyCommandSuffix = " request give {target}";

        static void Parse(Config c, string text)
        {
            var js = new JavaScriptSerializer();
            var root = js.Deserialize<Dictionary<string, object>>(text);
            if (root == null) return;

            c.MainEdid = Str(root, "mainEdid", c.MainEdid);
            string layout = Str(root, "layout", c.Layout).Trim().ToLowerInvariant();
            c.Layout = layout == "none" ? "none" : "second";
            c.DirectTake = Bool(root, "directTake", c.DirectTake);
            c.PollMs = Int(root, "pollMs", c.PollMs);
            c.Debounce = Int(root, "debounce", c.Debounce);
            c.SshExe = Str(root, "sshExe", c.SshExe);

            // 旧键在前，新键在后：两者都有时以新键为准
            var codes = Obj(root, "codes");
            c.SelfCode = Code(codes, "windows", c.SelfCode);
            c.PeerCode = Code(codes, "mac", c.PeerCode);
            uint[] other = UIntArray(codes, "other");
            if (other != null)
                foreach (uint code in other)
                    if (code >= 1 && code <= 255) c.Devices.Add(new Device(DefaultDeviceName(c.Devices.Count + 1), code));

            var self = Obj(root, "self");
            c.SelfName = NonBlank(Str(self, "name", c.SelfName), c.SelfName);
            c.SelfCode = Code(self, "code", c.SelfCode);

            var peer = Obj(root, "peer");
            string os = Str(peer, "os", c.PeerOs).Trim().ToLowerInvariant();
            c.PeerOs = os == "windows" ? "windows" : "mac";
            c.PeerName = NonBlank(Str(peer, "name", ""), DefaultPeerName(c.PeerOs));
            c.PeerHost = Str(peer, "host", c.PeerHost);
            c.PeerUser = Str(peer, "user", c.PeerUser);
            c.PeerCode = Code(peer, "code", c.PeerCode);
            c.PeerHotkey = Str(peer, "hotkey", c.PeerHotkey);
            c.PeerKeyPath = Str(peer, "keyPath", c.PeerKeyPath);
            c.PeerTimeoutSec = Int(peer, "timeoutSec", c.PeerTimeoutSec);
            string command = Str(peer, "command", "");
            if (command.EndsWith(LegacyCommandSuffix)) c.PeerProgram = command.Substring(0, command.Length - LegacyCommandSuffix.Length);
            c.PeerProgram = Str(peer, "program", c.PeerProgram);

            var devices = Get(root, "devices") as IEnumerable;
            if (devices != null && !(devices is string))
            {
                c.Devices.Clear();
                foreach (object o in devices)
                {
                    var d = o as Dictionary<string, object>;
                    uint code = Code(d, "code", 0);
                    if (code == 0) continue;
                    var dev = new Device(NonBlank(Str(d, "name", ""), DefaultDeviceName(c.Devices.Count + 1)), code);
                    dev.Hotkey = Str(d, "hotkey", "");
                    c.Devices.Add(dev);
                }
            }

            // 旧的 hotkeys 键忽略：快捷键默认不注册，要用就配 peer.hotkey 和各设备的 hotkey

            var rel = Obj(root, "release");
            c.ReleaseWidth = Int(rel, "width", c.ReleaseWidth);
            c.ReleaseHeight = Int(rel, "height", c.ReleaseHeight);

            var after = Obj(root, "afterTake");
            c.AfterTakeCommand = Str(after, "command", c.AfterTakeCommand);
            c.AfterTakeDelayMs = Int(after, "delayMs", c.AfterTakeDelayMs);
        }

        // 只写新键（设计文档 4.1），UTF-8 无 BOM；先写临时文件再覆盖
        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"mainEdid\": ").Append(Q(MainEdid)).Append(",\r\n");
            sb.Append("  \"layout\": ").Append(Q(Layout)).Append(",\r\n");
            sb.Append("  \"self\": { \"name\": ").Append(Q(SelfName)).Append(", \"code\": ").Append(SelfCode).Append(" },\r\n");
            sb.Append("  \"peer\": {\r\n");
            sb.Append("    \"name\": ").Append(Q(PeerName)).Append(", \"os\": ").Append(Q(PeerOs))
                .Append(", \"host\": ").Append(Q(PeerHost)).Append(", \"user\": ").Append(Q(PeerUser)).Append(",\r\n");
            sb.Append("    \"code\": ").Append(PeerCode).Append(", \"program\": ").Append(Q(PeerProgram))
                .Append(", \"hotkey\": ").Append(Q(PeerHotkey)).Append(",\r\n");
            sb.Append("    \"keyPath\": ").Append(Q(PeerKeyPath)).Append(", \"timeoutSec\": ").Append(PeerTimeoutSec).Append("\r\n");
            sb.Append("  },\r\n");
            sb.Append("  \"devices\": [");
            for (int i = 0; i < Devices.Count; i++)
            {
                Device d = Devices[i];
                sb.Append(i == 0 ? "\r\n" : ",\r\n");
                sb.Append("    { \"name\": ").Append(Q(d.Name)).Append(", \"code\": ").Append(d.Code)
                    .Append(", \"hotkey\": ").Append(Q(d.Hotkey)).Append(" }");
            }
            sb.Append(Devices.Count == 0 ? "],\r\n" : "\r\n  ],\r\n");
            sb.Append("  \"directTake\": ").Append(DirectTake ? "true" : "false").Append(",\r\n");
            sb.Append("  \"pollMs\": ").Append(PollMs).Append(", \"debounce\": ").Append(Debounce)
                .Append(", \"sshExe\": ").Append(Q(SshExe)).Append(",\r\n");
            sb.Append("  \"release\": { \"width\": ").Append(ReleaseWidth).Append(", \"height\": ").Append(ReleaseHeight).Append(" },\r\n");
            sb.Append("  \"afterTake\": { \"command\": ").Append(Q(AfterTakeCommand)).Append(", \"delayMs\": ").Append(AfterTakeDelayMs).Append(" }\r\n");
            sb.Append("}\r\n");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        // JSON 字符串：只转义必须转义的字符，中文等原样写出
        static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                if (ch == '"') sb.Append("\\\"");
                else if (ch == '\\') sb.Append("\\\\");
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v;
            return null;
        }
        static string Str(Dictionary<string, object> d, string key, string def)
        {
            object v = Get(d, key);
            return v == null ? def : v.ToString();
        }
        static string NonBlank(string s, string def) { return Blank(s) ? def : s; }
        static int Int(Dictionary<string, object> d, string key, int def)
        {
            object v = Get(d, key);
            if (v == null) return def;
            try { return Convert.ToInt32(v); }
            catch (Exception) { return def; }
        }
        static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            object v = Get(d, key);
            return v is bool ? (bool)v : def;
        }
        // 输入口编码：1-255 有效；缺失时用 def，写了但不在范围内算未知（0）
        static uint Code(Dictionary<string, object> d, string key, uint def)
        {
            if (Get(d, key) == null) return def;
            int v = Int(d, key, 0);
            return v >= 1 && v <= 255 ? (uint)v : 0;
        }
        static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            return Get(d, key) as Dictionary<string, object>;
        }
        static uint[] UIntArray(Dictionary<string, object> d, string key)
        {
            var e = Get(d, key) as IEnumerable;
            if (e == null || e is string) return null;
            var list = new List<uint>();
            try { foreach (object o in e) list.Add((uint)Convert.ToInt32(o)); }
            catch (Exception) { return null; }
            return list.ToArray();
        }
    }
}
