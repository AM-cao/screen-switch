using System;
using System.Collections.Generic;
using System.IO;

namespace ScreenSwitch
{
    // 命令层（docs/settings-ui-design.md 第 7 节）：所有设置的读写。命令行命令、设置窗口、向导都只调它。
    // 改配置的方法：成功返回 null，失败返回一句给人看的说明，配置文件不动。
    public class SettingsService
    {
        public readonly string Dir;
        readonly ISetupEnv env;
        readonly Autostart autostart;
        // 改完配置后让常驻程序重新读取。默认：托盘程序在运行时写命令文件 reload；设置窗口换成在进程内重新加载。
        public Action Reload;

        public SettingsService(string dir, ISetupEnv env, Autostart autostart)
        {
            Dir = dir; this.env = env; this.autostart = autostart;
            Reload = delegate { if (TrayRunning()) StateFile.WriteCommand(Dir, "reload"); };
        }

        public string ConfigPath { get { return Path.Combine(Dir, "config.json"); } }
        public string PrefsPath { get { return Path.Combine(Dir, "prefs.json"); } }
        public ISetupEnv Env { get { return env; } }

        // 配置损坏（LoadError）时 error 不为空：此时一律不改，免得用默认值覆盖用户的文件
        public Config Load(out string error)
        {
            Config c = Config.Load(ConfigPath);
            error = c.LoadError == null ? null : Tr.T("err.config.broken", c.LoadError);
            return c;
        }

        public bool TrayRunning()
        {
            bool owned; long ts;
            return StateFile.ReadState(Dir, out owned, out ts) && StateFile.Now() - ts <= 10;
        }

        public delegate string Change(Config c);

        public string Edit(Change change)
        {
            string err;
            Config c = Load(out err);
            if (err != null) return err;
            err = change(c);
            if (err != null) return err;
            c.Save(ConfigPath);
            if (Reload != null) Reload();
            return null;
        }

        // ---- 设备 ----

        public string AddDevice(uint code, string name)
        {
            string n = (name ?? "").Trim();
            return Edit(delegate(Config c)
            {
                if (code < 1 || code > 255) return Tr.T("err.device.code");
                if (n.Length == 0) return Tr.T("err.device.name");
                if (code == c.SelfCode) return Tr.T("err.device.self", code);
                if (code == c.PeerCode) return Tr.T("err.device.peer", code, c.PeerName);
                Device d = c.FindDevice(code);
                if (d != null) return Tr.T("err.device.taken", code, d.Name);
                c.Devices.Add(new Device(n, code));
                return null;
            });
        }

        public string RenameDevice(uint code, string name)
        {
            string n = (name ?? "").Trim();
            return Edit(delegate(Config c)
            {
                Device d = c.FindDevice(code);
                if (d == null) return Tr.T("err.device.notfound", code);
                if (n.Length == 0) return Tr.T("err.device.name");
                d.Name = n;
                return null;
            });
        }

        public string RemoveDevice(uint code)
        {
            return Edit(delegate(Config c)
            {
                Device d = c.FindDevice(code);
                if (d == null) return Tr.T("err.device.notfound", code);
                c.Devices.Remove(d);
                return null;
            });
        }

        public string SetPorts(IList<PortRow> rows)
        {
            return Edit(delegate(Config c) { return Ports.Apply(c, rows); });
        }

        // ---- 对方电脑 ----

        public string SetPeer(string name, string os, string host, string user, out string sshOutput)
        {
            sshOutput = "";
            string o = (os ?? "").Trim().ToLowerInvariant();
            if (o != "windows" && o != "mac") return Tr.T("err.peer.os");
            string h = (host ?? "").Trim();
            if (!Setup.ValidHost(h)) return Tr.T("err.peer.host");
            string u = Setup.CleanUser(user);
            if (u == null) return Tr.T("err.peer.user");
            string n = (name ?? "").Trim();
            string err = Edit(delegate(Config c)
            {
                if (!string.Equals(h, c.PeerHost, StringComparison.OrdinalIgnoreCase)) c.PeerCode = 0;
                if (o != c.PeerOs) c.PeerProgram = "";
                c.PeerOs = o; c.PeerHost = h; c.PeerUser = u;
                c.PeerName = n.Length > 0 ? n : Config.DefaultPeerName(o);
                return null;
            });
            if (err != null) return err;
            string output;
            int rc = env.SshClientSetup(u, h, out output);
            sshOutput = rc == -1 ? Tr.T("peer.ssh.manual", u, h) : (output ?? "");
            return null;
        }

        public string ClearPeer()
        {
            return Edit(delegate(Config c)
            {
                c.PeerHost = ""; c.PeerUser = ""; c.PeerCode = 0;
                return null;
            });
        }

        // ---- 其余设置 ----

        public string SetOption(string key, string[] values)
        {
            string v = values != null && values.Length > 0 ? values[0] : null;
            switch (key)
            {
                case "owned-layout":
                    if (v != "extend" && v != "main-only") return Bad(key, v);
                    return EditPrefs(delegate(Prefs p) { p.OwnedLayout = v == "main-only" ? OwnedLayout.MainOnly : OwnedLayout.Extend; });
                case "auto-save":
                    if (v != "on" && v != "off") return Bad(key, v);
                    return EditPrefs(delegate(Prefs p) { p.AutoSaveLayout = v == "on"; });
                case "direct-take":
                    if (v != "on" && v != "off") return Bad(key, v);
                    return Edit(delegate(Config c) { c.DirectTake = v == "on"; return null; });
                case "after-take":
                    if (v == null) return Bad(key, "");
                    return Edit(delegate(Config c) { c.AfterTakeCommand = v.Trim(); return null; });
                case "hotkey":
                    if (values == null || values.Length != 2) return Bad(key, string.Join(" ", values ?? new string[0]));
                    return SetHotkey(values[0], values[1]);
                default:
                    return Tr.T("err.option.unknown", key);
            }
        }

        static string Bad(string key, string v) { return Tr.T("err.option.value", key, v ?? ""); }

        delegate void PrefsChange(Prefs p);

        string EditPrefs(PrefsChange change)
        {
            Prefs p = Prefs.Load(PrefsPath);
            change(p);
            p.Save(PrefsPath);
            if (Reload != null) Reload();
            return null;
        }

        string SetHotkey(string target, string combo)
        {
            string hk = (combo ?? "").Trim();
            bool clear = hk.Length == 0 || hk.ToLowerInvariant() == "none";
            uint mods = 0, vk = 0;
            // 要带 Ctrl(2)、Alt(1) 或 Win(8)：只有 Shift 的组合会吞掉正常打字
            if (!clear && (!TrayApp.ParseHotkey(hk, out mods, out vk) || (mods & 0xB) == 0)) return Tr.T("err.hotkey.bad", hk);
            if (!clear) hk = Normalize(mods, vk);
            uint code = 0;
            if (target != "peer" && !Cli.ParseCode(target, out code)) return Bad("hotkey", target);
            return Edit(delegate(Config c)
            {
                Device dev = null;
                if (target != "peer")
                {
                    dev = c.FindDevice(code);
                    if (dev == null) return Tr.T("err.device.notfound", code);
                }
                if (!clear)
                {
                    // 同一个组合不能给两个动作
                    if (dev != null && Same(c.PeerHotkey, mods, vk)) return Tr.T("err.hotkey.taken", hk, Tr.T("hotkey.target.peer"));
                    foreach (Device other in c.Devices)
                        if (other != dev && Same(other.Hotkey, mods, vk)) return Tr.T("err.hotkey.taken", hk, other.Name);
                }
                if (dev == null) c.PeerHotkey = clear ? "" : hk;
                else dev.Hotkey = clear ? "" : hk;
                return null;
            });
        }

        static bool Same(string hotkey, uint mods, uint vk)
        {
            uint m, k;
            return !string.IsNullOrEmpty(hotkey) && TrayApp.ParseHotkey(hotkey, out m, out k) && m == mods && k == vk;
        }

        // 规范写法：Ctrl+Alt+Shift+Win+键（与设置窗口录下的写法一致）
        static string Normalize(uint mods, uint vk)
        {
            var parts = new List<string>();
            if ((mods & 2) != 0) parts.Add("Ctrl");
            if ((mods & 1) != 0) parts.Add("Alt");
            if ((mods & 4) != 0) parts.Add("Shift");
            if ((mods & 8) != 0) parts.Add("Win");
            parts.Add(vk >= 0x70 && vk <= 0x87 ? "F" + (vk - 0x70 + 1) : ((char)vk).ToString());
            return string.Join("+", parts.ToArray());
        }

        // ---- 开机自动运行 ----

        public bool AutostartEnabled { get { return autostart.Enabled; } }

        public string SetAutostart(bool on)
        {
            try
            {
                if (on) autostart.Enable(); else autostart.Disable();
                return null;
            }
            catch (Exception e) { return Tr.T("err.autostart", e.Message); }
        }

        // ---- 检测 ----

        public DetectResult Probe(string mainModel)
        {
            return Detection.Choose(env.Monitors(), env.ConnectedCount(), mainModel);
        }

        public DetectResult Detect(string mainModel)
        {
            DetectResult r = Probe(mainModel);
            if (r.Error != null) return r;
            r.Error = Edit(delegate(Config c)
            {
                ApplyDetect(c, r);
                return null;
            });
            if (r.Error == null) ClearDirectRecord();
            return r;
        }

        public static void ApplyDetect(Config c, DetectResult r)
        {
            c.MainEdid = r.MainEdid;
            c.SelfCode = r.SelfCode;
            c.Layout = r.Layout;
            if (c.PeerCode == c.SelfCode) c.PeerCode = 0;
            c.Devices.RemoveAll(delegate(Device d) { return d.Code == c.SelfCode; });
        }

        // 重新设置后清掉"直接切换从未成功"的记录（design.md 5.2），让托盘程序重新试
        void ClearDirectRecord()
        {
            try { File.Delete(Path.Combine(Dir, "direct-take.txt")); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---- 接口 ----

        public PortsResult ReadPorts(Config c)
        {
            List<uint> codes = env.InputCodes(c.MainEdid);
            var r = new PortsResult();
            r.MonitorKnown = codes != null;
            r.Rows = Ports.Build(codes, c);
            return r;
        }

        // ---- 配对 ----

        public PeerTestResult TestPeer()
        {
            string err;
            Config c = Load(out err);
            var r = new PeerTestResult();
            if (err != null) { r.Error = err; return r; }
            r = TestPeerWith(c);
            // 本机或某台设备的编码不存：两个用途占同一个口，配置就乱了
            if (r.Error == null && r.Code != 0 && r.Code != c.SelfCode && r.Code != c.PeerCode && c.FindDevice(r.Code) == null)
            {
                uint code = r.Code;
                r.CodeSaved = Edit(delegate(Config x) { x.PeerCode = code; return null; }) == null;
            }
            else if (r.Error == null && r.Code != 0 && r.Code == c.PeerCode) r.CodeSaved = true;
            return r;
        }

        public PeerTestResult TestPeerWith(Config c)
        {
            var r = new PeerTestResult();
            if (!c.PeerConfigured) { r.Error = Tr.T("peer.err.none"); return r; }
            PeerInfo info = env.RequestInfo(c);
            if (info == null) { r.Error = Tr.T("peer.err.unreachable", c.PeerName); return r; }
            r.Name = info.Name; r.Code = info.Code;
            return r;
        }

        public string PublicKey()
        {
            string k = env.PublicKey();
            if (k == null) k = env.GenerateKey();
            return Setup.SafeKey(k);
        }

        public string Authorize(string key, bool elevate, out string output)
        {
            output = "";
            string safe = Setup.SafeKey(key);
            if (safe == null) return Tr.T("auth.err.key");
            int rc = env.AuthorizePeerKey(safe, elevate, out output);
            if (rc == 0) return null;
            if (rc == 2) return Tr.T("auth.need-admin", "powershell -ExecutionPolicy Bypass -File \"" + Path.Combine(Dir, "setup-sshd.ps1") + "\" -PeerPubKey \"" + safe + "\"");
            if (rc == 1223) return Tr.T("auth.err.cancelled");
            if (rc == -1) return Tr.T("auth.err.missing");
            return Tr.T("auth.err.failed", rc);
        }

        // ---- 向导 ----

        public string SaveWizard(Config c, bool autostartOn)
        {
            bool written;
            return SaveWizard(c, autostartOn, out written);
        }

        // written：config.json 写成了没有（开机自动运行没设上时也是 true）。向导据此更新记下的文件时间。
        public string SaveWizard(Config c, bool autostartOn, out bool written)
        {
            written = false;
            try
            {
                // 现有的 config.json 读不了：先留一份 config.json.bak（盖掉旧的 .bak），再写新的
                if (File.Exists(ConfigPath) && Config.Load(ConfigPath).LoadError != null)
                    File.Copy(ConfigPath, ConfigPath + ".bak", true);
                c.Save(ConfigPath);
            }
            catch (Exception e) { return e.Message; }
            written = true;
            ClearDirectRecord();
            string err = SetAutostart(autostartOn);
            if (Reload != null) Reload();
            return err;
        }

        // ---- 状态 ----

        public Dictionary<string, object> Status()
        {
            string err;
            Config c = Load(out err);
            Prefs p = Prefs.Load(PrefsPath);
            bool owned; long ts;
            bool running = StateFile.ReadState(Dir, out owned, out ts) && StateFile.Now() - ts <= 10;
            bool configured = err == null && c.MainEdid != Config.Placeholder && c.SelfCode != 0;
            var problems = new List<string>();
            if (err != null) problems.Add(err);
            else if (!configured) problems.Add(Tr.T("problem.not-configured"));
            if (!running) problems.Add(Tr.T("problem.tray-not-running"));
            if (configured && c.PeerConfigured && c.PeerCode == 0) problems.Add(Tr.T("problem.peer-code-unknown", c.PeerName));

            var peer = new Dictionary<string, object>();
            peer["configured"] = c.PeerConfigured;
            peer["name"] = c.PeerName; peer["os"] = c.PeerOs; peer["host"] = c.PeerHost; peer["user"] = c.PeerUser;
            peer["code"] = (int)c.PeerCode;
            var devices = new List<Dictionary<string, object>>();
            foreach (Device dv in c.Devices)
            {
                var x = new Dictionary<string, object>();
                x["name"] = dv.Name; x["code"] = (int)dv.Code; x["hotkey"] = dv.Hotkey ?? "";
                devices.Add(x);
            }
            var self = new Dictionary<string, object>();
            self["name"] = c.SelfName; self["code"] = (int)c.SelfCode;

            var st = new Dictionary<string, object>();
            st["configured"] = configured;
            st["mainScreen"] = c.MainEdid;
            st["self"] = self;
            st["layout"] = c.Layout;
            st["ownedLayout"] = p.OwnedLayout == OwnedLayout.MainOnly ? "main-only" : "extend";
            st["autoSave"] = p.AutoSaveLayout;
            st["directTake"] = c.DirectTake;
            st["peer"] = peer;
            st["devices"] = devices;
            st["trayRunning"] = running;
            st["owned"] = running ? (object)owned : null;
            st["autostart"] = AutostartEnabled;
            st["problems"] = problems;
            return st;
        }
    }

    public class PortsResult
    {
        public bool MonitorKnown;
        public List<PortRow> Rows = new List<PortRow>();
    }

    public class PeerTestResult
    {
        public string Error;
        public string Name = "";
        public uint Code;
        public bool CodeSaved;
    }
}
