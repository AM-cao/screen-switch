using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace ScreenSwitch
{
    // 给 Agent 和脚本用的命令（docs/settings-ui-design.md 第 7 节），全部转给 SettingsService。
    // 成功退出码 0；失败 1，输出一行 "error: <原因>"。JSON 只用于 status、ports、peer test。
    public static class Commands
    {
        public const int NotMine = -1;

        public static int Run(string[] args, string dir, TextWriter output)
        {
            if (args.Length == 0 || !IsMine(args[0])) return NotMine;
            return Run(args, new SettingsService(dir, new SetupEnv(dir), Autostart.ForCurrentUser(dir)), output);
        }

        static bool IsMine(string c)
        {
            return c == "status" || c == "detect" || c == "ports" || c == "peer" || c == "pubkey" || c == "authorize"
                || c == "device" || c == "set" || c == "autostart";
        }

        public static int Run(string[] args, SettingsService s, TextWriter o)
        {
            if (args.Length == 0 || !IsMine(args[0])) return NotMine;
            switch (args[0])
            {
                case "status":
                    if (args.Length != 1) return Usage(o, "status");
                    o.WriteLine(Json(s.Status()));
                    return 0;
                case "detect": return Detect(args, s, o);
                case "ports": return PortsCmd(args, s, o);
                case "peer": return Peer(args, s, o);
                case "pubkey":
                    {
                        if (args.Length != 1) return Usage(o, "pubkey");
                        string k = s.PublicKey();
                        if (k == null) return Fail(o, Tr.T("auth.err.key"));
                        o.WriteLine(k);
                        return 0;
                    }
                case "authorize":
                    {
                        if (args.Length != 2) return Usage(o, "authorize \"<public key>\"");
                        string output;
                        return Done(o, s.Authorize(args[1], false, out output));
                    }
                case "device": return DeviceCmd(args, s, o);
                case "set":
                    {
                        if (args.Length < 3) return Usage(o, "set <key> <value>");
                        string key = args[1];
                        string[] values;
                        if (key == "after-take") values = new string[] { Join(args, 2) };
                        else { values = new string[args.Length - 2]; Array.Copy(args, 2, values, 0, values.Length); }
                        return Done(o, s.SetOption(key, values));
                    }
                case "autostart":
                    if (args.Length != 2 || (args[1] != "on" && args[1] != "off")) return Usage(o, "autostart on|off");
                    return Done(o, s.SetAutostart(args[1] == "on"));
            }
            return NotMine;
        }

        static int Detect(string[] args, SettingsService s, TextWriter o)
        {
            string model = null;
            if (args.Length == 3 && args[1] == "--main") model = args[2];
            else if (args.Length != 1) return Usage(o, "detect [--main <model>]");
            DetectResult r = s.Detect(model);
            if (r.Error == null)
            {
                o.WriteLine(Tr.T("cmd.ok"));
                return 0;
            }
            o.WriteLine("error: " + r.Error);
            if (r.Candidates.Count > 1)
            {
                o.WriteLine(Tr.T("cmd.detect.candidates"));
                foreach (SetupMonitor m in r.Candidates) o.WriteLine("  " + Setup.ModelId(m.DeviceId) + "  " + m.Name);
            }
            return 1;
        }

        static int PortsCmd(string[] args, SettingsService s, TextWriter o)
        {
            if (args.Length != 1) return Usage(o, "ports");
            string err;
            Config c = s.Load(out err);
            if (err != null) return Fail(o, err);
            PortsResult p = s.ReadPorts(c);
            var list = new List<Dictionary<string, object>>();
            foreach (PortRow r in p.Rows)
            {
                var x = new Dictionary<string, object>();
                x["code"] = (int)r.Code;
                x["name"] = Setup.InputName(r.Code);
                x["use"] = r.Use == PortUse.Self ? "self" : r.Use == PortUse.Peer ? "peer" : r.Use == PortUse.Device ? "device" : "none";
                x["device"] = r.Use == PortUse.Device ? r.DeviceName : "";
                x["fromMonitor"] = r.FromMonitor;
                list.Add(x);
            }
            var d = new Dictionary<string, object>();
            d["monitorKnown"] = p.MonitorKnown;
            d["ports"] = list;
            o.WriteLine(Json(d));
            return 0;
        }

        static int Peer(string[] args, SettingsService s, TextWriter o)
        {
            if (args.Length == 2 && args[1] == "clear") return Done(o, s.ClearPeer());
            if (args.Length == 2 && args[1] == "test")
            {
                PeerTestResult r = s.TestPeer();
                if (r.Error != null) return Fail(o, r.Error);
                var d = new Dictionary<string, object>();
                d["name"] = r.Name; d["code"] = (int)r.Code; d["codeSaved"] = r.CodeSaved;
                o.WriteLine(Json(d));
                return 0;
            }
            if (args.Length >= 2 && args[1] == "set")
            {
                string name = "", os = null, host = null, user = null;
                for (int i = 2; i < args.Length; i += 2)
                {
                    if (i + 1 >= args.Length) return Usage(o, "peer set --os <windows|mac> --host <address> --user <user> [--name <name>]");
                    string v = args[i + 1];
                    if (args[i] == "--name") name = v;
                    else if (args[i] == "--os") os = v;
                    else if (args[i] == "--host") host = v;
                    else if (args[i] == "--user") user = v;
                    else return Usage(o, "peer set --os <windows|mac> --host <address> --user <user> [--name <name>]");
                }
                if (os == null || host == null || user == null) return Usage(o, "peer set --os <windows|mac> --host <address> --user <user> [--name <name>]");
                string ssh;
                string err = s.SetPeer(name, os, host, user, out ssh);
                if (err != null) return Fail(o, err);
                if (ssh.Trim().Length > 0) o.WriteLine(ssh.Trim());
                o.WriteLine(Tr.T("cmd.ok"));
                return 0;
            }
            return Usage(o, "peer set ... | peer clear | peer test");
        }

        static int DeviceCmd(string[] args, SettingsService s, TextWriter o)
        {
            const string use = "device add <code> <name> | device rename <code> <name> | device remove <code>";
            if (args.Length < 3) return Usage(o, use);
            uint code;
            if (!Cli.ParseCode(args[2], out code)) return Fail(o, Tr.T("err.device.code"));
            if (args[1] == "add" && args.Length >= 4) return Done(o, s.AddDevice(code, Join(args, 3)));
            if (args[1] == "rename" && args.Length >= 4) return Done(o, s.RenameDevice(code, Join(args, 3)));
            if (args[1] == "remove" && args.Length == 3) return Done(o, s.RemoveDevice(code));
            return Usage(o, use);
        }

        static string Join(string[] a, int from)
        {
            var parts = new string[a.Length - from];
            Array.Copy(a, from, parts, 0, parts.Length);
            return string.Join(" ", parts);
        }

        static int Done(TextWriter o, string err)
        {
            if (err != null) return Fail(o, err);
            o.WriteLine(Tr.T("cmd.ok"));
            return 0;
        }

        static int Fail(TextWriter o, string err)
        {
            o.WriteLine("error: " + err.Replace("\r", " ").Replace("\n", " "));
            return 1;
        }

        static int Usage(TextWriter o, string use)
        {
            return Fail(o, Tr.T("cmd.usage", use));
        }

        public static string Json(object x)
        {
            return new JavaScriptSerializer().Serialize(x);
        }
    }
}
