using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ScreenSwitch
{
    // 当前拓扑：双屏扩展 / 克隆 / 只有主屏（副屏已连接但被关闭）/ 其他（缺屏、查询失败等）。
    public enum LayoutMode { Extend, Clone, MainOnly, Missing }
    // 拿回主屏后要应用的布局。
    public enum OwnedLayout { Extend, MainOnly }

    public interface ILayout
    {
        bool HasSnapshot { get; }
        LayoutMode Mode { get; }
        void SaveSnapshot();
        void Take(OwnedLayout target);
        void Release();
        // 当前拓扑为双屏扩展时返回其排列签名，否则 null
        string CurrentSignature();
        // 已保存快照的排列签名；快照缺失或损坏时 null
        string SnapshotSignature();
    }

    public class LayoutException : Exception
    {
        public LayoutException(string m) : base(m) { }
    }

    public class CcdLayout : ILayout
    {
        // ---- CCD 结构体（大小必须精确：PATH_INFO 72 字节，MODE_INFO 64 字节）----
        [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)] public struct RATIONAL { public uint Numerator; public uint Denominator; }
        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_TARGET_INFO
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling;
            public RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }
        [StructLayout(LayoutKind.Sequential)] public struct REGION2D { public uint cx; public uint cy; }
        [StructLayout(LayoutKind.Sequential)]
        public struct VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate; public RATIONAL hSyncFreq; public RATIONAL vSyncFreq; public REGION2D activeSize; public REGION2D totalSize;
            public uint videoStandard; public uint scanLineOrdering;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct SOURCE_MODE { public uint width; public uint height; public uint pixelFormat; public int x; public int y; }
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        public struct MODE_INFO
        {
            [FieldOffset(0)] public uint infoType;
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
            [FieldOffset(16)] public VIDEO_SIGNAL_INFO targetMode;
            [FieldOffset(16)] public SOURCE_MODE sourceMode;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TARGET_DEVICE_NAME
        {
            public DEVICE_INFO_HEADER header; public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, [Out] MODE_INFO[] modes, IntPtr topologyId);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, PATH_INFO[] paths, uint numModes, MODE_INFO[] modes, uint flags);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME req);

        const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE_PATHS = 2;
        const int ERROR_INSUFFICIENT_BUFFER = 122;
        const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_VALIDATE = 0x40, SDC_APPLY = 0x80, SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
        const uint APPLY_FLAGS = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE;
        const uint VALIDATE_FLAGS = SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES;
        const uint PATH_ACTIVE = 1;
        const uint MODE_SOURCE = 1, MODE_TARGET = 2;
        const uint MODE_IDX_INVALID = 0xFFFFFFFF;
        const uint ROTATION_IDENTITY = 1, SCALING_PREFERRED = 128, SCANLINE_UNSPECIFIED = 0, PIXELFORMAT_32BPP = 4;
        const uint DEVICE_INFO_GET_TARGET_NAME = 2;

        readonly string snapshotPath;
        readonly string mainEdid;
        readonly int relW, relH;
        readonly Log log;

        public CcdLayout(string snapshotPath, Config cfg, Log log)
        {
            this.snapshotPath = snapshotPath; mainEdid = cfg.MainEdid; relW = cfg.ReleaseWidth; relH = cfg.ReleaseHeight; this.log = log;
        }

        public bool HasSnapshot { get { return File.Exists(snapshotPath); } }

        // 当前拓扑。查询失败写日志并返回 Missing。
        public LayoutMode Mode
        {
            get
            {
                try
                {
                    PATH_INFO[] paths; MODE_INFO[] modes;
                    QueryActive(out paths, out modes);
                    return ModeOf(paths, modes);
                }
                catch (LayoutException e) { log.Error("layout mode: " + e.Message); return LayoutMode.Missing; }
            }
        }

        // 按活动路径判断拓扑：两条是克隆（见 IsClone）= 克隆；两条不同源且恰有一条是主屏 = 扩展；
        // 只有主屏一条且副屏仍连接可用 = 仅主屏；其余（缺屏、主屏不在拓扑里等）= Missing。
        LayoutMode ModeOf(PATH_INFO[] paths, MODE_INFO[] modes)
        {
            if (paths.Length == 2)
            {
                if (IsClone(paths[0], paths[1], modes)) return LayoutMode.Clone;
                int mains = (IsMain(paths[0]) ? 1 : 0) + (IsMain(paths[1]) ? 1 : 0);
                return mains == 1 ? LayoutMode.Extend : LayoutMode.Missing;
            }
            if (paths.Length == 1 && IsMain(paths[0]) && SecondaryAvailable()) return LayoutMode.MainOnly;
            return LayoutMode.Missing;
        }

        // 同一个源要连适配器一起比：两块屏分属两块显卡时，两边的源编号通常都是 0。
        static bool SameSource(PATH_INFO a, PATH_INFO b)
        {
            return a.sourceInfo.id == b.sourceInfo.id
                && a.sourceInfo.adapterId.LowPart == b.sourceInfo.adapterId.LowPart
                && a.sourceInfo.adapterId.HighPart == b.sourceInfo.adapterId.HighPart;
        }

        // 纯函数：两条活动路径是不是克隆。同一个源 = 克隆。两块屏分属两块显卡时一个源不能同时输出到两边，
        // 复制是两个源显示桌面的同一块区域：两个源模式的位置相同也算克隆（扩展时两个源的位置必然不同）。
        // 后一种情况没有真机验证过。
        public static bool IsClone(PATH_INFO a, PATH_INFO b, MODE_INFO[] modes)
        {
            if (SameSource(a, b)) return true;
            uint ia = a.sourceInfo.modeInfoIdx, ib = b.sourceInfo.modeInfoIdx;
            if (modes == null || ia == MODE_IDX_INVALID || ib == MODE_IDX_INVALID || ia >= modes.Length || ib >= modes.Length) return false;
            if (modes[ia].infoType != MODE_SOURCE || modes[ib].infoType != MODE_SOURCE) return false;
            return modes[ia].sourceMode.x == modes[ib].sourceMode.x && modes[ia].sourceMode.y == modes[ib].sourceMode.y;
        }

        // 副屏是否已连接可用：全部路径（含未激活的）里存在目标可用且不是主屏的路径。
        bool SecondaryAvailable()
        {
            PATH_INFO[] all; MODE_INFO[] modes;
            Query(QDC_ALL_PATHS, out all, out modes);
            foreach (PATH_INFO p in all)
                if (p.targetInfo.targetAvailable != 0 && !IsMain(p)) return true;
            return false;
        }

        // ---- 查询 ----
        // 缓冲区大小按同一个 flags 取；两次调用之间拓扑变化会返回 ERROR_INSUFFICIENT_BUFFER，重新取大小再查。
        static void Query(uint flags, out PATH_INFO[] paths, out MODE_INFO[] modes)
        {
            for (int attempt = 1; ; attempt++)
            {
                uint np, nm;
                int rc = GetDisplayConfigBufferSizes(flags, out np, out nm);
                if (rc != 0) throw new LayoutException("GetDisplayConfigBufferSizes rc=" + rc);
                paths = new PATH_INFO[np]; modes = new MODE_INFO[nm];
                rc = QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
                if (rc == ERROR_INSUFFICIENT_BUFFER && attempt < 3) continue;
                if (rc != 0) throw new LayoutException("QueryDisplayConfig rc=" + rc);
                Array.Resize(ref paths, (int)np); Array.Resize(ref modes, (int)nm);
                return;
            }
        }

        static void QueryActive(out PATH_INFO[] paths, out MODE_INFO[] modes)
        {
            Query(QDC_ONLY_ACTIVE_PATHS, out paths, out modes);
        }

        static string TargetDevicePath(LUID adapter, uint targetId)
        {
            var n = new TARGET_DEVICE_NAME();
            n.header.type = DEVICE_INFO_GET_TARGET_NAME;
            n.header.size = (uint)Marshal.SizeOf(typeof(TARGET_DEVICE_NAME));
            n.header.adapterId = adapter; n.header.id = targetId;
            int rc = DisplayConfigGetDeviceInfo(ref n);
            return rc == 0 ? (n.monitorDevicePath ?? "") : "";
        }

        bool IsMain(PATH_INFO p)
        {
            return IsMainDevicePath(TargetDevicePath(p.targetInfo.adapterId, p.targetInfo.id), mainEdid);
        }

        // 纯函数：目标设备路径里含有主屏的 EDID 标识（不分大小写）即为主屏。
        public static bool IsMainDevicePath(string devicePath, string mainEdid)
        {
            return devicePath != null && devicePath.IndexOf(mainEdid, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 快照的第 i 条路径是不是主屏：新快照看保存时记下的设备路径；
        // 旧快照没有设备路径，按（已替换 LUID 的）路径向系统查询。
        bool SnapIsMain(PATH_INFO[] snapPaths, string[] devicePaths, int i)
        {
            return devicePaths != null ? IsMainDevicePath(devicePaths[i], mainEdid) : IsMain(snapPaths[i]);
        }

        // ---- 快照 ----
        public void SaveSnapshot()
        {
            PATH_INFO[] paths; MODE_INFO[] modes;
            QueryActive(out paths, out modes);
            if (paths.Length != 2) throw new LayoutException("需要恰好两块活动显示器，当前 " + paths.Length + " 块");
            if (IsClone(paths[0], paths[1], modes)) throw new LayoutException("当前是克隆模式，请先切回扩展模式再保存");
            // 各路径的目标设备路径一并存下：应用时靠它在当前路径里找回各自的适配器
            var devs = new string[paths.Length];
            int mainCount = 0;
            for (int i = 0; i < paths.Length; i++)
            {
                devs[i] = TargetDevicePath(paths[i].targetInfo.adapterId, paths[i].targetInfo.id);
                if (IsMainDevicePath(devs[i], mainEdid)) mainCount++;
            }
            if (mainCount != 1) throw new LayoutException("没有找到主屏 " + mainEdid);
            string tmpPath = snapshotPath + ".tmp";
            File.WriteAllBytes(tmpPath, SnapshotToBytes(paths, modes, devs));
            File.Copy(tmpPath, snapshotPath, true);
            File.Delete(tmpPath);
            log.Info("layout snapshot saved: " + paths.Length + " paths, " + modes.Length + " modes");
        }

        const string CorruptMsg = "布局快照损坏，请在双屏状态下重新保存布局";

        // 快照文件：int32 路径数，int32 模式数，各 PATH_INFO，各 MODE_INFO —— 旧快照到此为止。
        // 新快照后面接一段尾部：标记 "TDP1"，int32 条数（等于路径数），再依次是每条路径的目标设备路径
        //（int32 字节数 + UTF-8）。文件必须正好在这里结束。
        static readonly byte[] TrailerMagic = { 0x54, 0x44, 0x50, 0x31 };
        static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        const int MAX_DEVICE_PATH_BYTES = 1024, MAX_SNAPSHOT_BYTES = 65536;

        // devicePaths：各路径保存时的目标设备路径；旧快照为 null。
        void LoadSnapshot(out PATH_INFO[] paths, out MODE_INFO[] modes, out string[] devicePaths)
        {
            if (!HasSnapshot) throw new LayoutException("布局快照不存在，请先在双屏状态下保存布局");
            byte[] data;
            try
            {
                if (new FileInfo(snapshotPath).Length > MAX_SNAPSHOT_BYTES) throw new LayoutException(CorruptMsg);
                data = File.ReadAllBytes(snapshotPath);
            }
            catch (IOException) { throw new LayoutException(CorruptMsg); }
            SnapshotFromBytes(data, out paths, out modes, out devicePaths);
        }

        // 纯函数：快照 → 文件内容。devicePaths 为 null 时写成旧格式（没有尾部）。
        public static byte[] SnapshotToBytes(PATH_INFO[] paths, MODE_INFO[] modes, string[] devicePaths)
        {
            if (devicePaths != null && devicePaths.Length != paths.Length) throw new ArgumentException("devicePaths");
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(paths.Length); bw.Write(modes.Length);
                bw.Write(ToBytes(paths)); bw.Write(ToBytes(modes));
                if (devicePaths != null)
                {
                    bw.Write(TrailerMagic); bw.Write(devicePaths.Length);
                    foreach (string d in devicePaths)
                    {
                        byte[] b = Encoding.UTF8.GetBytes(d ?? "");
                        bw.Write(b.Length); bw.Write(b);
                    }
                }
                bw.Flush();
                return ms.ToArray();
            }
        }

        // 纯函数：文件内容 → 快照。长度正好是旧格式 = 旧快照，devicePaths 为 null；
        // 尾部完整 = 新快照；其余（截断、标记不对、条数不对、多出字节等）一律按损坏处理。
        public static void SnapshotFromBytes(byte[] data, out PATH_INFO[] paths, out MODE_INFO[] modes, out string[] devicePaths)
        {
            if (data == null) throw new LayoutException(CorruptMsg);
            int pathSize = Marshal.SizeOf(typeof(PATH_INFO));
            int modeSize = Marshal.SizeOf(typeof(MODE_INFO));
            try
            {
                using (var ms = new MemoryStream(data))
                using (var br = new BinaryReader(ms))
                {
                    int np = br.ReadInt32(), nm = br.ReadInt32();
                    if (np != 2 || nm < 1 || nm > 64) throw new LayoutException(CorruptMsg);
                    int oldLen = 8 + np * pathSize + nm * modeSize;
                    if (data.Length < oldLen) throw new LayoutException(CorruptMsg);
                    paths = FromBytes<PATH_INFO>(br.ReadBytes(np * pathSize), np);
                    modes = FromBytes<MODE_INFO>(br.ReadBytes(nm * modeSize), nm);
                    devicePaths = null;
                    if (data.Length == oldLen) return;

                    byte[] magic = br.ReadBytes(TrailerMagic.Length);
                    if (magic.Length != TrailerMagic.Length) throw new LayoutException(CorruptMsg);
                    for (int i = 0; i < magic.Length; i++)
                        if (magic[i] != TrailerMagic[i]) throw new LayoutException(CorruptMsg);
                    if (br.ReadInt32() != np) throw new LayoutException(CorruptMsg);
                    devicePaths = new string[np];
                    for (int i = 0; i < np; i++)
                    {
                        int len = br.ReadInt32();
                        if (len < 0 || len > MAX_DEVICE_PATH_BYTES) throw new LayoutException(CorruptMsg);
                        byte[] b = br.ReadBytes(len);
                        if (b.Length != len) throw new LayoutException(CorruptMsg);
                        devicePaths[i] = StrictUtf8.GetString(b);
                    }
                    if (ms.Position != data.Length) throw new LayoutException(CorruptMsg);
                }
            }
            catch (EndOfStreamException) { throw new LayoutException(CorruptMsg); }
            catch (DecoderFallbackException) { throw new LayoutException(CorruptMsg); }
        }

        // 适配器 LUID 每次开机都会变，应用前替换成当前的。
        // 新快照：每条路径按保存时的目标设备路径在当前全部路径里找到自己的适配器（两块屏可以分属两块显卡）；
        // 找不到的那条退回旧办法并记日志。旧快照：全部换成第一条活动路径的适配器。
        void PatchCurrentLuids(PATH_INFO[] paths, MODE_INFO[] modes, string[] devicePaths)
        {
            PATH_INFO[] cur; MODE_INFO[] curModes;
            QueryActive(out cur, out curModes);
            if (cur.Length == 0) throw new LayoutException("当前没有活动显示路径");
            LUID legacy = cur[0].sourceInfo.adapterId;
            string[] curDevs = null; LUID[] curLuids = null;
            if (devicePaths != null) CurrentTargets(out curDevs, out curLuids);
            bool[] found = PatchLuids(paths, modes, devicePaths, curDevs, curLuids, legacy);
            if (devicePaths == null) return;
            for (int i = 0; i < found.Length; i++)
                if (!found[i]) log.Info("layout: snapshot target not among the current paths, using the first active adapter: " + devicePaths[i]);
        }

        // 当前全部路径（含未激活的，例如仅主屏时被关掉的副屏）里目标可用的那些：设备路径与所在的适配器。
        // 同一个目标会出现在多条路径里，只查一次。
        static void CurrentTargets(out string[] devicePaths, out LUID[] luids)
        {
            PATH_INFO[] all; MODE_INFO[] modes;
            Query(QDC_ALL_PATHS, out all, out modes);
            var seen = new List<string>();
            var devs = new List<string>();
            var ids = new List<LUID>();
            foreach (PATH_INFO p in all)
            {
                if (p.targetInfo.targetAvailable == 0) continue;
                LUID a = p.targetInfo.adapterId;
                string key = a.HighPart + ":" + a.LowPart + ":" + p.targetInfo.id;
                if (seen.Contains(key)) continue;
                seen.Add(key);
                devs.Add(TargetDevicePath(a, p.targetInfo.id));
                ids.Add(a);
            }
            devicePaths = devs.ToArray(); luids = ids.ToArray();
        }

        // 设置问答用：当前已连接（目标可用）的显示器，含没点亮的：设备路径与 EDID 里的名字（取不到为空串）。
        // 查询失败抛 LayoutException。
        public static void ConnectedTargets(out string[] devicePaths, out string[] friendlyNames)
        {
            PATH_INFO[] all; MODE_INFO[] modes;
            Query(QDC_ALL_PATHS, out all, out modes);
            var seen = new List<string>();
            var devs = new List<string>();
            var names = new List<string>();
            foreach (PATH_INFO p in all)
            {
                if (p.targetInfo.targetAvailable == 0) continue;
                LUID a = p.targetInfo.adapterId;
                string key = a.HighPart + ":" + a.LowPart + ":" + p.targetInfo.id;
                if (seen.Contains(key)) continue;
                seen.Add(key);
                var n = new TARGET_DEVICE_NAME();
                n.header.type = DEVICE_INFO_GET_TARGET_NAME;
                n.header.size = (uint)Marshal.SizeOf(typeof(TARGET_DEVICE_NAME));
                n.header.adapterId = a; n.header.id = p.targetInfo.id;
                bool ok = DisplayConfigGetDeviceInfo(ref n) == 0;
                devs.Add(ok ? (n.monitorDevicePath ?? "") : "");
                names.Add(ok ? (n.monitorFriendlyDeviceName ?? "") : "");
            }
            devicePaths = devs.ToArray(); friendlyNames = names.ToArray();
        }

        // 纯函数：把快照里的适配器 LUID 换成当前的。先按旧办法全部换成 legacy（第一条活动路径的适配器）；
        // devicePaths 不为 null 时，每条路径再按设备路径（不分大小写）在当前目标 curDevicePaths / curLuids 里
        // 找到自己的适配器，换掉这条路径的源、目标，以及它引用的源模式、目标模式。
        // 返回各路径是否找到；没找到的（以及保存时没取到设备路径的）保持 legacy。
        public static bool[] PatchLuids(PATH_INFO[] paths, MODE_INFO[] modes, string[] devicePaths, string[] curDevicePaths, LUID[] curLuids, LUID legacy)
        {
            for (int i = 0; i < paths.Length; i++) { paths[i].sourceInfo.adapterId = legacy; paths[i].targetInfo.adapterId = legacy; }
            for (int i = 0; i < modes.Length; i++) modes[i].adapterId = legacy;
            var found = new bool[paths.Length];
            if (devicePaths == null || curDevicePaths == null || curLuids == null) return found;
            for (int i = 0; i < paths.Length && i < devicePaths.Length; i++)
            {
                if (string.IsNullOrEmpty(devicePaths[i])) continue;
                for (int j = 0; j < curDevicePaths.Length && j < curLuids.Length; j++)
                {
                    if (!string.Equals(devicePaths[i], curDevicePaths[j], StringComparison.OrdinalIgnoreCase)) continue;
                    LUID luid = curLuids[j];
                    paths[i].sourceInfo.adapterId = luid; paths[i].targetInfo.adapterId = luid;
                    uint si = paths[i].sourceInfo.modeInfoIdx, ti = paths[i].targetInfo.modeInfoIdx;
                    if (si != MODE_IDX_INVALID && si < modes.Length) modes[si].adapterId = luid;
                    if (ti != MODE_IDX_INVALID && ti < modes.Length) modes[ti].adapterId = luid;
                    found[i] = true;
                    break;
                }
            }
            return found;
        }

        // ---- 拿回 ----
        public void Take(OwnedLayout target)
        {
            if (target == OwnedLayout.MainOnly) TakeMainOnly();
            else TakeExtend();
        }

        // 双屏扩展：重放快照，失败回退 DisplaySwitch /extend。
        void TakeExtend()
        {
            PATH_INFO[] paths; MODE_INFO[] modes; string[] devs;
            LoadSnapshot(out paths, out modes, out devs);
            PatchCurrentLuids(paths, modes, devs);
            int rc = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, APPLY_FLAGS);
            if (rc == 0) { log.Info("layout take: ok"); return; }
            int vrc = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, VALIDATE_FLAGS);
            log.Error("layout take: SetDisplayConfig rc=" + rc + " (validate rc=" + vrc + "), falling back to DisplaySwitch /extend");
            Fallback("/extend", false);
        }

        // 仅主屏：只提交快照里主屏那一条路径（源模式移到 (0,0)），
        // SDC_USE_SUPPLIED_DISPLAY_CONFIG 下未列出的副屏路径即被关闭。失败不回退 DisplaySwitch。
        void TakeMainOnly()
        {
            PATH_INFO[] snapPaths; MODE_INFO[] snapModes; string[] devs;
            LoadSnapshot(out snapPaths, out snapModes, out devs);
            PatchCurrentLuids(snapPaths, snapModes, devs);
            int mainIdx = -1, mainCount = 0;
            for (int i = 0; i < snapPaths.Length; i++)
                if (SnapIsMain(snapPaths, devs, i)) { mainIdx = i; mainCount++; }
            // 找不到（或找到两条）主屏时不能猜，否则可能把主屏关掉
            if (mainCount != 1) throw new LayoutException("快照中没有找到主屏 " + mainEdid);
            PATH_INFO[] paths; MODE_INFO[] modes;
            MainOnlyConfig(snapPaths, snapModes, mainIdx, out paths, out modes);

            int rc = SetDisplayConfig(1, paths, 2, modes, APPLY_FLAGS);
            if (rc != 0)
            {
                int vrc = SetDisplayConfig(1, paths, 2, modes, VALIDATE_FLAGS);
                log.Error("layout take main-only: SetDisplayConfig rc=" + rc + " (validate rc=" + vrc + ")");
                throw new LayoutException("关闭副屏失败 rc=" + rc);
            }
            // 显示器正在切换输入（例如刚被切回本机）时拓扑会短暂读成 Missing（实测 1–3 秒）：
            // 读到 Missing 就每 250 毫秒重查，最多 3 秒；读到扩展/克隆或到时仍是 Missing 才算失败。
            int waited;
            LayoutMode now = WaitWhileMissing(delegate { return Mode; }, Thread.Sleep, MAIN_ONLY_SETTLE_MS, MAIN_ONLY_POLL_MS, out waited);
            if (waited > 0) log.Info("layout take main-only: mode read Missing, " + now + " after " + waited + " ms");
            if (now != LayoutMode.MainOnly)
            {
                log.Error("layout take main-only: applied but mode is " + now);
                throw new LayoutException("关闭副屏后拓扑不是仅主屏（当前 " + now + "）");
            }
            log.Info("layout take main-only: ok");
        }

        const int MAIN_ONLY_SETTLE_MS = 3000, MAIN_ONLY_POLL_MS = 250;

        // 纯函数（查询与等待由调用方提供）：拓扑读到 Missing 时每 stepMs 重查一次，最多累计等待 timeoutMs；
        // 读到其他拓扑立即返回。返回最后一次读到的拓扑，waitedMs 为累计等待。
        public static LayoutMode WaitWhileMissing(Func<LayoutMode> query, Action<int> sleep, int timeoutMs, int stepMs, out int waitedMs)
        {
            waitedMs = 0;
            LayoutMode m = query();
            while (m == LayoutMode.Missing && waitedMs < timeoutMs)
            {
                sleep(stepMs); waitedMs += stepMs;
                m = query();
            }
            return m;
        }

        // 纯函数：从快照构造只含主屏一条路径的配置。新模式数组 0 = 主屏源模式（位置改为 (0,0)），
        // 1 = 主屏目标模式（保留原刷新率等时序）；路径的两个模式索引随之改为 0 和 1。快照数组不被改动。
        public static void MainOnlyConfig(PATH_INFO[] snapPaths, MODE_INFO[] snapModes, int mainIdx, out PATH_INFO[] paths, out MODE_INFO[] modes)
        {
            if (mainIdx < 0 || mainIdx >= snapPaths.Length) throw new LayoutException("快照中没有找到主屏");
            PATH_INFO pm = snapPaths[mainIdx];
            uint si = pm.sourceInfo.modeInfoIdx, ti = pm.targetInfo.modeInfoIdx;
            if (si == MODE_IDX_INVALID || si >= snapModes.Length || snapModes[si].infoType != MODE_SOURCE)
                throw new LayoutException("快照中主屏没有源模式");
            if (ti == MODE_IDX_INVALID || ti >= snapModes.Length || snapModes[ti].infoType != MODE_TARGET)
                throw new LayoutException("快照中主屏没有目标模式");

            modes = new MODE_INFO[2];
            modes[0] = snapModes[si];
            modes[0].id = pm.sourceInfo.id; modes[0].adapterId = pm.sourceInfo.adapterId;
            modes[0].sourceMode.x = 0; modes[0].sourceMode.y = 0;
            modes[1] = snapModes[ti];
            modes[1].id = pm.targetInfo.id; modes[1].adapterId = pm.targetInfo.adapterId;

            paths = new PATH_INFO[1];
            paths[0] = pm;
            paths[0].sourceInfo.modeInfoIdx = 0; paths[0].sourceInfo.statusFlags = 0;
            paths[0].targetInfo.modeInfoIdx = 1; paths[0].targetInfo.statusFlags = 0;
            paths[0].flags = PATH_ACTIVE;
        }

        // ---- 让出：副屏的源同时输出到副屏和主屏（克隆）----
        public void Release()
        {
            PATH_INFO[] snapPaths; MODE_INFO[] snapModes; string[] devs;
            LoadSnapshot(out snapPaths, out snapModes, out devs);
            PatchCurrentLuids(snapPaths, snapModes, devs);
            int mainIdx = SnapIsMain(snapPaths, devs, 0) ? 0 : 1;
            PATH_INFO[] paths; MODE_INFO[] modes;
            ReleaseConfig(snapPaths, snapModes, mainIdx, relW, relH, out paths, out modes);

            int rc = SetDisplayConfig(2, paths, 2, modes, APPLY_FLAGS);
            if (rc == 0) { log.Info("layout release: ok"); return; }
            int vrc = SetDisplayConfig(2, paths, 2, modes, VALIDATE_FLAGS);
            log.Error("layout release: SetDisplayConfig rc=" + rc + " (validate rc=" + vrc + "), falling back to DisplaySwitch /clone");
            Fallback("/clone", true);
        }

        // 纯函数：从快照构造让出用的克隆配置。新模式数组 0 = 副屏的源模式（位置 (0,0)；分辨率为 relW x relH，
        // 其中任一不是正数时取快照里副屏源模式自己的分辨率），1 = 副屏的目标模式；
        // 路径 0 = 副屏，路径 1 = 同一个源输出到主屏（目标模式交给系统选）。快照数组不被改动。
        public static void ReleaseConfig(PATH_INFO[] snapPaths, MODE_INFO[] snapModes, int mainIdx, int relW, int relH, out PATH_INFO[] paths, out MODE_INFO[] modes)
        {
            if (snapPaths.Length != 2 || mainIdx < 0 || mainIdx > 1) throw new LayoutException("快照中没有找到主屏");
            PATH_INFO pm = snapPaths[mainIdx];
            PATH_INFO ps = snapPaths[1 - mainIdx];
            if (ps.targetInfo.modeInfoIdx == MODE_IDX_INVALID || ps.targetInfo.modeInfoIdx >= snapModes.Length)
                throw new LayoutException("快照中副屏没有目标模式");
            uint w, h;
            if (relW > 0 && relH > 0) { w = (uint)relW; h = (uint)relH; }
            else
            {
                uint si = ps.sourceInfo.modeInfoIdx;
                if (si == MODE_IDX_INVALID || si >= snapModes.Length || snapModes[si].infoType != MODE_SOURCE
                    || snapModes[si].sourceMode.width == 0 || snapModes[si].sourceMode.height == 0)
                    throw new LayoutException("快照中副屏没有源模式（release 为 0 时要用它的分辨率）");
                w = snapModes[si].sourceMode.width; h = snapModes[si].sourceMode.height;
            }

            modes = new MODE_INFO[2];
            modes[0] = new MODE_INFO();
            modes[0].infoType = MODE_SOURCE; modes[0].id = ps.sourceInfo.id; modes[0].adapterId = ps.sourceInfo.adapterId;
            modes[0].sourceMode.width = w; modes[0].sourceMode.height = h;
            modes[0].sourceMode.pixelFormat = PIXELFORMAT_32BPP; modes[0].sourceMode.x = 0; modes[0].sourceMode.y = 0;
            modes[1] = snapModes[ps.targetInfo.modeInfoIdx];
            modes[1].infoType = MODE_TARGET; modes[1].id = ps.targetInfo.id;

            paths = new PATH_INFO[2];
            paths[0] = ps;
            paths[0].sourceInfo.modeInfoIdx = 0; paths[0].sourceInfo.statusFlags = 0;
            paths[0].targetInfo.modeInfoIdx = 1; paths[0].targetInfo.statusFlags = 0;
            paths[0].flags = PATH_ACTIVE;

            paths[1] = new PATH_INFO();
            paths[1].sourceInfo = ps.sourceInfo; paths[1].sourceInfo.modeInfoIdx = 0; paths[1].sourceInfo.statusFlags = 0;
            paths[1].targetInfo.adapterId = pm.targetInfo.adapterId; paths[1].targetInfo.id = pm.targetInfo.id;
            paths[1].targetInfo.outputTechnology = pm.targetInfo.outputTechnology;
            paths[1].targetInfo.modeInfoIdx = MODE_IDX_INVALID;
            paths[1].targetInfo.rotation = ROTATION_IDENTITY; paths[1].targetInfo.scaling = SCALING_PREFERRED;
            paths[1].targetInfo.refreshRate.Numerator = 0; paths[1].targetInfo.refreshRate.Denominator = 0;
            paths[1].targetInfo.scanLineOrdering = SCANLINE_UNSPECIFIED; paths[1].targetInfo.targetAvailable = 1; paths[1].targetInfo.statusFlags = 0;
            paths[1].flags = PATH_ACTIVE;
        }

        // 回退只负责切换拓扑，并在切换后校验结果。
        static void Fallback(string arg, bool expectClone)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("DisplaySwitch.exe", arg) { UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (p != null) p.WaitForExit(5000);
                }
                Thread.Sleep(3000);
            }
            catch (Exception e) { throw new LayoutException("DisplaySwitch " + arg + " 失败: " + e.Message); }
            PATH_INFO[] paths; MODE_INFO[] modes;
            QueryActive(out paths, out modes);
            bool cloned = paths.Length == 2 && IsClone(paths[0], paths[1], modes);
            bool extended = paths.Length == 2 && !cloned;
            if (expectClone ? !cloned : !extended) throw new LayoutException("回退切换失败：拓扑未改变");
        }

        // ---- 诊断 ----
        public string Show()
        {
            PATH_INFO[] paths; MODE_INFO[] modes;
            QueryActive(out paths, out modes);
            var sb = new StringBuilder();
            sb.AppendLine("struct sizes: PATH_INFO=" + Marshal.SizeOf(typeof(PATH_INFO)) + " MODE_INFO=" + Marshal.SizeOf(typeof(MODE_INFO)) + " TARGET_DEVICE_NAME=" + Marshal.SizeOf(typeof(TARGET_DEVICE_NAME)));
            sb.AppendLine("mode: " + Mode);
            sb.AppendLine("active paths: " + paths.Length + ", modes: " + modes.Length + ", snapshot: " + HasSnapshot);
            foreach (PATH_INFO p in paths)
            {
                string dev = TargetDevicePath(p.targetInfo.adapterId, p.targetInfo.id);
                string src = "";
                if (p.sourceInfo.modeInfoIdx != MODE_IDX_INVALID && p.sourceInfo.modeInfoIdx < modes.Length)
                {
                    SOURCE_MODE sm = modes[p.sourceInfo.modeInfoIdx].sourceMode;
                    src = sm.width + "x" + sm.height + " @(" + sm.x + "," + sm.y + ")";
                }
                string hz = p.targetInfo.refreshRate.Denominator == 0 ? "?" : ((double)p.targetInfo.refreshRate.Numerator / p.targetInfo.refreshRate.Denominator).ToString("0.##");
                sb.AppendLine("  source " + p.sourceInfo.id + " -> target " + p.targetInfo.id + " " + src + " " + hz + "Hz main=" + IsMain(p) + " " + dev);
            }
            return sb.ToString();
        }

        // ---- 排列签名 ----
        public string CurrentSignature()
        {
            try
            {
                PATH_INFO[] paths; MODE_INFO[] modes;
                QueryActive(out paths, out modes);
                if (ModeOf(paths, modes) != LayoutMode.Extend) return null;
                return Signature(paths, modes);
            }
            catch (LayoutException e) { log.Error("layout current signature: " + e.Message); return null; }
        }

        public string SnapshotSignature()
        {
            try
            {
                PATH_INFO[] paths; MODE_INFO[] modes; string[] devs;
                LoadSnapshot(out paths, out modes, out devs);
                return Signature(paths, modes);
            }
            catch (LayoutException) { return null; }
        }

        // 纯函数：每条路径一段 "t<目标id> <宽>x<高>@<x>,<y> r<旋转> <刷新率>Hz"（宽高位置取该路径的源模式），
        // 按序数排序后用 | 连接。不查询硬件、不判断主屏，快照与当前拓扑按同一规则比较。
        public static string Signature(PATH_INFO[] paths, MODE_INFO[] modes)
        {
            var parts = new string[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                PATH_INFO p = paths[i];
                uint idx = p.sourceInfo.modeInfoIdx;
                if (idx == MODE_IDX_INVALID || idx >= modes.Length || modes[idx].infoType != MODE_SOURCE)
                    throw new LayoutException("显示路径的源模式索引无效：" + idx);
                SOURCE_MODE sm = modes[idx].sourceMode;
                RATIONAL r = p.targetInfo.refreshRate;
                string hz = r.Denominator == 0 ? "?" : ((double)r.Numerator / r.Denominator).ToString("0.0", CultureInfo.InvariantCulture);
                parts[i] = "t" + Inv(p.targetInfo.id) + " " + Inv(sm.width) + "x" + Inv(sm.height)
                    + "@" + Inv(sm.x) + "," + Inv(sm.y) + " r" + Inv(p.targetInfo.rotation) + " " + hz + "Hz";
            }
            Array.Sort(parts, StringComparer.Ordinal);
            return string.Join("|", parts);
        }

        static string Inv(long v) { return v.ToString(CultureInfo.InvariantCulture); }

        // ---- 序列化 ----
        public static byte[] ToBytes<T>(T[] arr) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            var buf = new byte[size * arr.Length];
            IntPtr tmp = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    Marshal.StructureToPtr(arr[i], tmp, false);
                    Marshal.Copy(tmp, buf, i * size, size);
                }
            }
            finally { Marshal.FreeHGlobal(tmp); }
            return buf;
        }

        public static T[] FromBytes<T>(byte[] buf, int count) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            var arr = new T[count];
            IntPtr tmp = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Marshal.Copy(buf, i * size, tmp, size);
                    arr[i] = (T)Marshal.PtrToStructure(tmp, typeof(T));
                }
            }
            finally { Marshal.FreeHGlobal(tmp); }
            return arr;
        }
    }
}
