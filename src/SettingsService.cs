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
            if (!clear && (!TrayApp.ParseHotkey(hk, out mods, out vk) || mods == 0)) return Tr.T("err.hotkey.bad", hk);
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
    }
}
