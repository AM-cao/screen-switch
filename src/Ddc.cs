using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

namespace ScreenSwitch
{
    public interface IDdc
    {
        bool TryReadInput(out uint value);
        bool WriteInput(uint value);
        string Describe();
    }

    // 设置问答用：一块已点亮的显示器的探测结果
    public class MonitorProbe
    {
        // EnumDisplayDevices 给的接口名，与 CCD 的目标设备路径是同一种写法
        public string DeviceId = "";
        public string Description = "";
        // 读 VCP 0x60 成功
        public bool DdcOk;
        public uint Input;
    }

    public class DxvaDdc : IDdc
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE dd, uint dwFlags);
        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint cur, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr h, byte code, uint val);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetCapabilitiesStringLength(IntPtr h, out uint len);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, [Out] byte[] buf, uint len);

        const byte VCP_INPUT = 0x60;
        const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;
        const uint DISPLAY_DEVICE_ACTIVE = 0x1;
        // 一个逻辑显示器下最多枚举这么多物理监视器。
        const uint MaxMonitorsPerAdapter = 8;
        // capabilities 字符串一般几百字节；超过这个长度当作读错了。
        const uint MaxCapabilitiesLength = 16384;

        readonly string mainEdid;
        readonly Log log;

        // 最近一次 VCP 0x60 读取失败时的 Win32 错误码（成功为 0），供 diag 使用。
        public int LastError;

        public DxvaDdc(string mainEdid, Log log) { this.mainEdid = mainEdid; this.log = log; }

        bool Locate(out IntPtr hmon, out int index) { return Locate(out hmon, out index, null); }

        // 把某个逻辑显示器下枚举到的 DeviceID/StateFlags 列表映射到 dxva2 物理监视器数组下标：
        // 只统计带 DISPLAY_DEVICE_ACTIVE 的项（未激活的项不占用物理监视器数组的槽位），
        // 返回第一条已激活的主屏项在“仅激活项”里的序号；未激活的主屏项跳过、继续往后找；
        // 找不到已激活的主屏项返回 -1。
        public static int PhysicalIndex(IList<string> deviceIds, IList<uint> stateFlags, string mainEdid)
        {
            int activeCount = 0;
            for (int i = 0; i < deviceIds.Count; i++)
            {
                bool active = (stateFlags[i] & DISPLAY_DEVICE_ACTIVE) != 0;
                if (!active) continue;
                string deviceId = deviceIds[i];
                bool isMain = deviceId != null && mainEdid != null && deviceId.IndexOf(mainEdid, StringComparison.OrdinalIgnoreCase) >= 0;
                if (isMain) return activeCount;
                activeCount++;
            }
            return -1;
        }

        // 找到主屏：遍历逻辑显示器，用 EnumDisplayDevices 列出其下的物理监视器 DeviceID/StateFlags，
        // 用 PhysicalIndex 算出主屏在“仅激活项”里对应的 dxva2 物理监视器数组下标。
        // 传入 sb 时把枚举到的每一项都写进去，并遍历完所有显示器（诊断用，一趟枚举拿全部信息）。
        bool Locate(out IntPtr hmon, out int index, StringBuilder sb)
        {
            hmon = IntPtr.Zero; index = -1;
            bool found = false;
            var list = new List<IntPtr>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr hdc, IntPtr r, IntPtr d) { list.Add(h); return true; }, IntPtr.Zero);
            foreach (IntPtr h in list)
            {
                var mi = new MONITORINFOEX();
                mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!GetMonitorInfo(h, ref mi)) continue;
                var deviceIds = new List<string>();
                var stateFlags = new List<uint>();
                for (uint i = 0; i < MaxMonitorsPerAdapter; i++)
                {
                    var dd = new DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    if (!EnumDisplayDevices(mi.szDevice, i, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) break;
                    if (sb != null) sb.AppendLine("display=" + mi.szDevice + " index=" + i + " deviceId=" + dd.DeviceID + " flags=0x" + dd.StateFlags.ToString("X"));
                    deviceIds.Add(dd.DeviceID);
                    stateFlags.Add(dd.StateFlags);
                }
                if (!found)
                {
                    int physicalIndex = PhysicalIndex(deviceIds, stateFlags, mainEdid);
                    if (physicalIndex >= 0)
                    {
                        hmon = h; index = physicalIndex; found = true;
                        if (sb == null) return true;
                    }
                }
            }
            return found;
        }

        // 用已定位的逻辑显示器打开物理监视器数组；用完必须 DestroyPhysicalMonitors。
        bool OpenAt(IntPtr hmon, int index, out PHYSICAL_MONITOR[] arr)
        {
            arr = null;
            uint n;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out n) || n == 0 || index >= n) return false;
            var a = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(hmon, n, a)) return false;
            arr = a;
            return true;
        }

        // 打开主屏的物理监视器句柄；用完必须 DestroyPhysicalMonitors。
        bool Open(out PHYSICAL_MONITOR[] arr, out int index)
        {
            arr = null; index = -1;
            IntPtr hmon;
            if (!Locate(out hmon, out index)) return false;
            return OpenAt(hmon, index, out arr);
        }

        bool ReadInputAt(IntPtr hmon, int index, out uint value)
        {
            value = 0;
            PHYSICAL_MONITOR[] arr;
            if (!OpenAt(hmon, index, out arr)) return false;
            try
            {
                uint type, cur, max;
                bool ok = GetVCPFeatureAndVCPFeatureReply(arr[index].hPhysicalMonitor, VCP_INPUT, out type, out cur, out max);
                LastError = ok ? 0 : Marshal.GetLastWin32Error();
                value = cur;
                return ok;
            }
            finally { DestroyPhysicalMonitors((uint)arr.Length, arr); }
        }

        public bool TryReadInput(out uint value)
        {
            value = 0;
            IntPtr hmon; int idx;
            if (!Locate(out hmon, out idx)) return false;
            return ReadInputAt(hmon, idx, out value);
        }

        public bool WriteInput(uint value)
        {
            PHYSICAL_MONITOR[] arr; int idx;
            if (!Open(out arr, out idx)) { log.Error("DDC: main monitor not found"); return false; }
            try
            {
                bool ok = SetVCPFeature(arr[idx].hPhysicalMonitor, VCP_INPUT, value);
                log.Info("DDC write 0x" + value.ToString("X") + " -> " + ok);
                return ok;
            }
            finally { DestroyPhysicalMonitors((uint)arr.Length, arr); }
        }

        // ---- 设置问答用：枚举显示器、读 capabilities ----

        // 枚举所有已点亮的物理监视器，逐个读 VCP 0x60。物理监视器数组的下标与"仅激活项"的序号对应（同 PhysicalIndex）。
        public static List<MonitorProbe> ProbeAll()
        {
            var result = new List<MonitorProbe>();
            var list = new List<IntPtr>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr hdc, IntPtr r, IntPtr d) { list.Add(h); return true; }, IntPtr.Zero);
            foreach (IntPtr h in list)
            {
                var mi = new MONITORINFOEX();
                mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!GetMonitorInfo(h, ref mi)) continue;
                var ids = new List<string>();
                for (uint i = 0; i < MaxMonitorsPerAdapter; i++)
                {
                    var dd = new DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    if (!EnumDisplayDevices(mi.szDevice, i, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) break;
                    if ((dd.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0) ids.Add(dd.DeviceID);
                }
                uint n;
                PHYSICAL_MONITOR[] arr = null;
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(h, out n) && n > 0)
                {
                    arr = new PHYSICAL_MONITOR[n];
                    if (!GetPhysicalMonitorsFromHMONITOR(h, n, arr)) arr = null;
                }
                try
                {
                    for (int k = 0; k < ids.Count; k++)
                    {
                        var p = new MonitorProbe();
                        p.DeviceId = ids[k] ?? "";
                        if (arr != null && k < arr.Length)
                        {
                            p.Description = arr[k].szPhysicalMonitorDescription ?? "";
                            p.DdcOk = ProbeInput(arr[k].hPhysicalMonitor, out p.Input);
                        }
                        result.Add(p);
                    }
                }
                finally { if (arr != null) DestroyPhysicalMonitors((uint)arr.Length, arr); }
            }
            return result;
        }

        // 托盘程序也在轮询同一块屏，偶尔会撞上：失败后隔一会儿再读一次。
        static bool ProbeInput(IntPtr hPhysical, out uint value)
        {
            value = 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) System.Threading.Thread.Sleep(150);
                uint type, cur, max;
                if (GetVCPFeatureAndVCPFeatureReply(hPhysical, VCP_INPUT, out type, out cur, out max)) { value = cur; return true; }
            }
            return false;
        }

        // 主屏的 capabilities 字符串。要一两秒；读不到（显示器不提供、读取出错）返回 null。
        public string ReadCapabilities()
        {
            PHYSICAL_MONITOR[] arr; int idx;
            if (!Open(out arr, out idx)) return null;
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    uint len;
                    if (!GetCapabilitiesStringLength(arr[idx].hPhysicalMonitor, out len) || len == 0 || len > MaxCapabilitiesLength) continue;
                    var buf = new byte[len];
                    if (!CapabilitiesRequestAndCapabilitiesReply(arr[idx].hPhysicalMonitor, buf, len)) continue;
                    int end = Array.IndexOf(buf, (byte)0);
                    return Encoding.ASCII.GetString(buf, 0, end < 0 ? buf.Length : end);
                }
                return null;
            }
            finally { DestroyPhysicalMonitors((uint)arr.Length, arr); }
        }

        // 纯函数：capabilities 字符串里 VCP 0x60 支持的取值（按出现顺序，去重，只留 1–255）。
        // 形如 …vcp(02 14(05 08 0B) 60(0F 10 11 12) D6(01 04))…：十六进制，特性后面可以带一组取值。
        // 没有 vcp 段、没有 0x60、0x60 不带取值，都返回空表。
        public static List<uint> ParseInputCodes(string caps)
        {
            var result = new List<uint>();
            if (caps == null) return result;
            int i = VcpSectionStart(caps);
            if (i < 0) return result;
            while (i < caps.Length && caps[i] != ')')
            {
                if (caps[i] == '(') { i = GroupEnd(caps, i) + 1; continue; }
                if (!IsHex(caps[i])) { i++; continue; }
                int j = i;
                while (j < caps.Length && IsHex(caps[j])) j++;
                string feature = caps.Substring(i, j - i);
                int k = j;
                while (k < caps.Length && char.IsWhiteSpace(caps[k])) k++;
                if (k >= caps.Length || caps[k] != '(') { i = j; continue; }
                int end = GroupEnd(caps, k);
                if (feature == "60") AddHexValues(result, caps.Substring(k + 1, end - k - 1));
                i = end + 1;
            }
            return result;
        }

        // vcp( 之后第一个字符的位置；没有 vcp 段返回 -1。vcpname( 是另一个段。
        static int VcpSectionStart(string caps)
        {
            for (int at = 0; (at = caps.IndexOf("vcp", at, StringComparison.OrdinalIgnoreCase)) >= 0; at += 3)
            {
                if (at > 0 && char.IsLetterOrDigit(caps[at - 1])) continue;
                int k = at + 3;
                while (k < caps.Length && char.IsWhiteSpace(caps[k])) k++;
                if (k < caps.Length && caps[k] == '(') return k + 1;
            }
            return -1;
        }

        // open 处是 '('：返回与它配对的 ')' 的位置；字符串被截断时返回末尾。
        static int GroupEnd(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')' && --depth == 0) return i;
            }
            return s.Length;
        }

        static bool IsHex(char ch) { return (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'); }

        // 取值列表：空格分隔的十六进制；有的显示器不写空格（0F1011），按两位一组拆开。
        static void AddHexValues(List<uint> result, string values)
        {
            int i = 0;
            while (i < values.Length)
            {
                if (values[i] == '(') { i = GroupEnd(values, i) + 1; continue; }
                if (!IsHex(values[i])) { i++; continue; }
                int j = i;
                while (j < values.Length && IsHex(values[j])) j++;
                string token = values.Substring(i, j - i);
                i = j;
                if (token.Length > 2 && token.Length % 2 != 0) continue;
                for (int p = 0; p < token.Length; p += 2)
                {
                    uint v = Convert.ToUInt32(token.Substring(p, Math.Min(2, token.Length - p)), 16);
                    if (v >= 1 && v <= 255 && !result.Contains(v)) result.Add(v);
                }
            }
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            IntPtr hmon; int idx;
            bool found = Locate(out hmon, out idx, sb);
            sb.AppendLine("configured mainEdid=" + mainEdid);
            sb.AppendLine("main monitor located: " + found + (found ? " hmon=" + hmon + " physicalIndex=" + idx : ""));
            uint v = 0;
            bool ok = found && ReadInputAt(hmon, idx, out v);
            sb.AppendLine("VCP 0x60 read: ok=" + ok + " value=0x" + v.ToString("X") + (ok ? "" : " lastError=0x" + LastError.ToString("X")));
            return sb.ToString();
        }
    }
}
