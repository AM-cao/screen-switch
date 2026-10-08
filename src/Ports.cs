using System.Collections.Generic;
using System.Globalization;

namespace ScreenSwitch
{
    public enum PortUse { None, Self, Peer, Device }

    // 接口表的一行（docs/settings-ui-design.md 第 4、5 节）
    public class PortRow
    {
        public uint Code;
        public PortUse Use;
        public string DeviceName = "";
        // 显示器的 capabilities 里有这个口；false = 只是配置里登记过，或用户手动添加
        public bool FromMonitor;
        public PortRow() { }
        public PortRow(uint code, PortUse use, string deviceName, bool fromMonitor)
        {
            Code = code; Use = use; DeviceName = deviceName ?? ""; FromMonitor = fromMonitor;
        }
    }

    public static class Ports
    {
        // 口的显示名：标准名称加编码，例如"HDMI 1（17）"；不认识的只显示编码
        public static string Label(uint code)
        {
            string n = Setup.InputName(code);
            return n.Length > 0 ? Tr.T("port.label", n, code) : code.ToString(CultureInfo.InvariantCulture);
        }

        // monitorCodes：主屏 capabilities 里 0x60 的取值，null = 读不出。再补上配置里登记过的口，按编码排序。
        public static List<PortRow> Build(IList<uint> monitorCodes, Config cfg)
        {
            var rows = new List<PortRow>();
            if (monitorCodes != null)
                foreach (uint c in monitorCodes)
                    if (c >= 1 && c <= 255 && Find(rows, c) == null) rows.Add(new PortRow(c, PortUse.None, "", true));
            Ensure(rows, cfg.SelfCode);
            Ensure(rows, cfg.PeerCode);
            foreach (Device d in cfg.Devices) Ensure(rows, d.Code);
            foreach (PortRow r in rows)
            {
                Device d = cfg.FindDevice(r.Code);
                if (r.Code == cfg.SelfCode) r.Use = PortUse.Self;
                else if (r.Code == cfg.PeerCode) r.Use = PortUse.Peer;
                else if (d != null) { r.Use = PortUse.Device; r.DeviceName = d.Name; }
            }
            rows.Sort(delegate(PortRow a, PortRow b) { return a.Code.CompareTo(b.Code); });
            return rows;
        }

        static void Ensure(List<PortRow> rows, uint code)
        {
            if (code >= 1 && code <= 255 && Find(rows, code) == null) rows.Add(new PortRow(code, PortUse.None, "", false));
        }

        public static PortRow Find(IList<PortRow> rows, uint code)
        {
            foreach (PortRow r in rows) if (r.Code == code) return r;
            return null;
        }

        // 把接口表写回配置：对方编码、设备列表（同编码的设备保留原来的快捷键）。不合法时返回说明，配置不动。
        public static string Apply(Config cfg, IList<PortRow> rows)
        {
            var seen = new List<uint>();
            uint peer = 0;
            int selfRows = 0;
            foreach (PortRow r in rows)
            {
                if (r.Code < 1 || r.Code > 255) return Tr.T("ports.err.code");
                if (seen.Contains(r.Code)) return Tr.T("ports.err.duplicate", r.Code);
                seen.Add(r.Code);
                if (r.Use == PortUse.Self)
                {
                    if (r.Code != cfg.SelfCode) return Tr.T("ports.err.self");
                    selfRows++;
                }
                if (r.Use == PortUse.Peer)
                {
                    if (peer != 0) return Tr.T("ports.err.two-peers");
                    peer = r.Code;
                }
                if (r.Use == PortUse.Device && (r.DeviceName ?? "").Trim().Length == 0) return Tr.T("ports.err.name", Label(r.Code));
            }
            if (cfg.SelfCode != 0 && selfRows != 1) return Tr.T("ports.err.self");
            var devices = new List<Device>();
            foreach (PortRow r in rows)
            {
                if (r.Use != PortUse.Device) continue;
                var d = new Device(r.DeviceName.Trim(), r.Code);
                Device old = cfg.FindDevice(r.Code);
                if (old != null) d.Hotkey = old.Hotkey;
                devices.Add(d);
            }
            cfg.PeerCode = peer;
            cfg.Devices = devices;
            return null;
        }
    }
}
