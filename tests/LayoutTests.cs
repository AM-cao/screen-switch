using System;
using System.Runtime.InteropServices;

namespace ScreenSwitch.Tests
{
    public static class LayoutTests
    {
        public static void Test_Layout_SnapshotBytesRoundTrip()
        {
            Assert.Eq(72, Marshal.SizeOf(typeof(CcdLayout.PATH_INFO)), "PATH_INFO size");
            Assert.Eq(64, Marshal.SizeOf(typeof(CcdLayout.MODE_INFO)), "MODE_INFO size");

            var p = new CcdLayout.PATH_INFO();
            p.sourceInfo.adapterId.LowPart = 0x11223344;
            p.sourceInfo.adapterId.HighPart = 0x55667788;
            p.sourceInfo.id = 3;
            p.sourceInfo.modeInfoIdx = 1;
            p.sourceInfo.statusFlags = 7;
            p.targetInfo.adapterId.LowPart = 0x99AABBCC;
            p.targetInfo.adapterId.HighPart = 0x0D0E0F10;
            p.targetInfo.id = 0x50001;
            p.targetInfo.modeInfoIdx = 2;
            p.targetInfo.outputTechnology = 11;
            p.targetInfo.rotation = 1;
            p.targetInfo.scaling = 128;
            p.targetInfo.refreshRate.Numerator = 160000;
            p.targetInfo.refreshRate.Denominator = 1001;
            p.targetInfo.scanLineOrdering = 2;
            p.targetInfo.targetAvailable = 1;
            p.targetInfo.statusFlags = 0x40001;
            p.flags = 1;

            var target = new CcdLayout.MODE_INFO();
            target.infoType = 2;
            target.id = 0x50001;
            target.adapterId.LowPart = 0xAABBCCDD;
            target.adapterId.HighPart = 0x12345678;
            target.targetMode.pixelRate = 1234567890123UL;
            target.targetMode.hSyncFreq.Numerator = 270000;
            target.targetMode.hSyncFreq.Denominator = 7;
            target.targetMode.vSyncFreq.Numerator = 160000;
            target.targetMode.vSyncFreq.Denominator = 1001;
            target.targetMode.activeSize.cx = 3840;
            target.targetMode.activeSize.cy = 2160;
            target.targetMode.totalSize.cx = 4000;
            target.targetMode.totalSize.cy = 2200;
            target.targetMode.videoStandard = 5;
            target.targetMode.scanLineOrdering = 1;

            var source = new CcdLayout.MODE_INFO();
            source.infoType = 1;
            source.id = 4;
            source.adapterId.LowPart = 0x0BADF00D;
            source.adapterId.HighPart = 0x7FFFFFFF;
            source.sourceMode.width = 2560;
            source.sourceMode.height = 1440;
            source.sourceMode.pixelFormat = 4;
            source.sourceMode.x = -1920;
            source.sourceMode.y = 137;

            byte[] pathBytes = CcdLayout.ToBytes(new CcdLayout.PATH_INFO[] { p });
            byte[] modeBytes = CcdLayout.ToBytes(new CcdLayout.MODE_INFO[] { target, source });
            Assert.Eq(72, pathBytes.Length, "path bytes");
            Assert.Eq(128, modeBytes.Length, "mode bytes");

            CcdLayout.PATH_INFO q = CcdLayout.FromBytes<CcdLayout.PATH_INFO>(pathBytes, 1)[0];
            Assert.Eq(p.sourceInfo.adapterId.LowPart, q.sourceInfo.adapterId.LowPart, "src luid low");
            Assert.Eq(p.sourceInfo.adapterId.HighPart, q.sourceInfo.adapterId.HighPart, "src luid high");
            Assert.Eq(p.sourceInfo.id, q.sourceInfo.id, "src id");
            Assert.Eq(p.sourceInfo.modeInfoIdx, q.sourceInfo.modeInfoIdx, "src modeIdx");
            Assert.Eq(p.sourceInfo.statusFlags, q.sourceInfo.statusFlags, "src status");
            Assert.Eq(p.targetInfo.adapterId.LowPart, q.targetInfo.adapterId.LowPart, "tgt luid low");
            Assert.Eq(p.targetInfo.adapterId.HighPart, q.targetInfo.adapterId.HighPart, "tgt luid high");
            Assert.Eq(p.targetInfo.id, q.targetInfo.id, "tgt id");
            Assert.Eq(p.targetInfo.modeInfoIdx, q.targetInfo.modeInfoIdx, "tgt modeIdx");
            Assert.Eq(p.targetInfo.outputTechnology, q.targetInfo.outputTechnology, "tgt tech");
            Assert.Eq(p.targetInfo.rotation, q.targetInfo.rotation, "tgt rotation");
            Assert.Eq(p.targetInfo.scaling, q.targetInfo.scaling, "tgt scaling");
            Assert.Eq(p.targetInfo.refreshRate.Numerator, q.targetInfo.refreshRate.Numerator, "tgt hz num");
            Assert.Eq(p.targetInfo.refreshRate.Denominator, q.targetInfo.refreshRate.Denominator, "tgt hz den");
            Assert.Eq(p.targetInfo.scanLineOrdering, q.targetInfo.scanLineOrdering, "tgt scanline");
            Assert.Eq(p.targetInfo.targetAvailable, q.targetInfo.targetAvailable, "tgt available");
            Assert.Eq(p.targetInfo.statusFlags, q.targetInfo.statusFlags, "tgt status");
            Assert.Eq(p.flags, q.flags, "path flags");

            CcdLayout.MODE_INFO[] modes = CcdLayout.FromBytes<CcdLayout.MODE_INFO>(modeBytes, 2);
            CcdLayout.MODE_INFO t2 = modes[0], s2 = modes[1];
            Assert.Eq(target.infoType, t2.infoType, "target infoType");
            Assert.Eq(target.id, t2.id, "target id");
            Assert.Eq(target.adapterId.LowPart, t2.adapterId.LowPart, "target luid low");
            Assert.Eq(target.adapterId.HighPart, t2.adapterId.HighPart, "target luid high");
            Assert.Eq(target.targetMode.pixelRate, t2.targetMode.pixelRate, "pixelRate");
            Assert.Eq(target.targetMode.hSyncFreq.Numerator, t2.targetMode.hSyncFreq.Numerator, "hsync num");
            Assert.Eq(target.targetMode.hSyncFreq.Denominator, t2.targetMode.hSyncFreq.Denominator, "hsync den");
            Assert.Eq(target.targetMode.vSyncFreq.Numerator, t2.targetMode.vSyncFreq.Numerator, "vsync num");
            Assert.Eq(target.targetMode.vSyncFreq.Denominator, t2.targetMode.vSyncFreq.Denominator, "vsync den");
            Assert.Eq(target.targetMode.activeSize.cx, t2.targetMode.activeSize.cx, "active cx");
            Assert.Eq(target.targetMode.activeSize.cy, t2.targetMode.activeSize.cy, "active cy");
            Assert.Eq(target.targetMode.totalSize.cx, t2.targetMode.totalSize.cx, "total cx");
            Assert.Eq(target.targetMode.totalSize.cy, t2.targetMode.totalSize.cy, "total cy");
            Assert.Eq(target.targetMode.videoStandard, t2.targetMode.videoStandard, "video standard");
            Assert.Eq(target.targetMode.scanLineOrdering, t2.targetMode.scanLineOrdering, "mode scanline");
            Assert.Eq(source.infoType, s2.infoType, "source infoType");
            Assert.Eq(source.id, s2.id, "source id");
            Assert.Eq(source.adapterId.LowPart, s2.adapterId.LowPart, "source luid low");
            Assert.Eq(source.adapterId.HighPart, s2.adapterId.HighPart, "source luid high");
            Assert.Eq(source.sourceMode.width, s2.sourceMode.width, "width");
            Assert.Eq(source.sourceMode.height, s2.sourceMode.height, "height");
            Assert.Eq(source.sourceMode.pixelFormat, s2.sourceMode.pixelFormat, "pixelFormat");
            Assert.Eq(source.sourceMode.x, s2.sourceMode.x, "x");
            Assert.Eq(source.sourceMode.y, s2.sourceMode.y, "y");
        }

        // ---- 排列签名 ----
        static CcdLayout.PATH_INFO SigPath(uint targetId, uint srcIdx, uint num, uint den)
        {
            var p = new CcdLayout.PATH_INFO();
            p.sourceInfo.modeInfoIdx = srcIdx;
            p.targetInfo.id = targetId;
            p.targetInfo.modeInfoIdx = 0xFFFFFFFF;
            p.targetInfo.rotation = 1;
            p.targetInfo.refreshRate.Numerator = num;
            p.targetInfo.refreshRate.Denominator = den;
            p.flags = 1;
            return p;
        }

        static CcdLayout.MODE_INFO SigSource(uint w, uint h, int x, int y)
        {
            var m = new CcdLayout.MODE_INFO();
            m.infoType = 1;
            m.sourceMode.width = w; m.sourceMode.height = h; m.sourceMode.pixelFormat = 4;
            m.sourceMode.x = x; m.sourceMode.y = y;
            return m;
        }

        static CcdLayout.MODE_INFO SigTarget()
        {
            var m = new CcdLayout.MODE_INFO();
            m.infoType = 2;
            m.targetMode.activeSize.cx = 1; m.targetMode.activeSize.cy = 1;
            return m;
        }

        // 典型双屏扩展：模式数组里源、目标模式交错，路径按索引引用源模式。
        static void SigFixture(out CcdLayout.PATH_INFO[] paths, out CcdLayout.MODE_INFO[] modes)
        {
            modes = new CcdLayout.MODE_INFO[] { SigTarget(), SigSource(3840, 2160, 0, 0), SigTarget(), SigSource(2560, 1440, -2560, 360) };
            paths = new CcdLayout.PATH_INFO[] { SigPath(4353, 1, 160000, 1000), SigPath(4352, 3, 170000, 1000) };
        }

        public static void Test_Signature_FormatSortedAndJoined()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            SigFixture(out paths, out modes);
            Assert.Eq("t4352 2560x1440@-2560,360 r1 170.0Hz|t4353 3840x2160@0,0 r1 160.0Hz",
                CcdLayout.Signature(paths, modes), "signature format");
        }

        public static void Test_Signature_IndependentOfPathOrder()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            SigFixture(out paths, out modes);
            string a = CcdLayout.Signature(paths, modes);
            var reversed = new CcdLayout.PATH_INFO[] { paths[1], paths[0] };
            Assert.Eq(a, CcdLayout.Signature(reversed, modes), "path order does not matter");
        }

        public static void Test_Signature_RefreshOneDecimalInvariant()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            SigFixture(out paths, out modes);
            paths[0].targetInfo.refreshRate.Numerator = 160000;
            paths[0].targetInfo.refreshRate.Denominator = 1001;
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                // 小数点用逗号的区域设置下也必须输出 159.8
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                string s = CcdLayout.Signature(paths, modes);
                Assert.True(s.EndsWith("|t4353 3840x2160@0,0 r1 159.8Hz"), "159.8Hz, got " + s);
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = saved; }
        }

        public static void Test_Signature_ZeroDenominatorIsQuestionMark()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            SigFixture(out paths, out modes);
            paths[1].targetInfo.refreshRate.Numerator = 0;
            paths[1].targetInfo.refreshRate.Denominator = 0;
            Assert.Eq("t4352 2560x1440@-2560,360 r1 ?Hz|t4353 3840x2160@0,0 r1 160.0Hz",
                CcdLayout.Signature(paths, modes), "unknown refresh");
        }

        public static void Test_Signature_ChangesWithPositionResolutionRotation()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            SigFixture(out paths, out modes);
            string baseSig = CcdLayout.Signature(paths, modes);

            modes[3].sourceMode.x = 3840; modes[3].sourceMode.y = 0;
            string moved = CcdLayout.Signature(paths, modes);
            Assert.True(moved != baseSig, "position change");

            SigFixture(out paths, out modes);
            modes[1].sourceMode.width = 2560; modes[1].sourceMode.height = 1440;
            Assert.True(CcdLayout.Signature(paths, modes) != baseSig, "resolution change");

            SigFixture(out paths, out modes);
            paths[1].targetInfo.rotation = 2;
            Assert.True(CcdLayout.Signature(paths, modes) != baseSig, "rotation change");

            SigFixture(out paths, out modes);
            paths[1].targetInfo.refreshRate.Numerator = 144000;
            Assert.True(CcdLayout.Signature(paths, modes) != baseSig, "refresh change");
        }

        public static void Test_Signature_InvalidSourceModeIndexThrows()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            SigFixture(out paths, out modes);
            paths[0].sourceInfo.modeInfoIdx = 0xFFFFFFFF;
            Assert.Throws<LayoutException>(delegate { CcdLayout.Signature(paths, modes); }, "invalid index");

            SigFixture(out paths, out modes);
            paths[1].sourceInfo.modeInfoIdx = 4;
            Assert.Throws<LayoutException>(delegate { CcdLayout.Signature(paths, modes); }, "index past end");

            // 索引指向的是目标模式而不是源模式
            SigFixture(out paths, out modes);
            paths[1].sourceInfo.modeInfoIdx = 2;
            Assert.Throws<LayoutException>(delegate { CcdLayout.Signature(paths, modes); }, "index at a target mode");
        }

        // ---- 仅主屏配置 ----
        // 与实机快照同样的形状：主屏路径 0（目标模式在 0、源模式在 1），副屏路径 1（目标模式 2、源模式 3）。
        static void SnapFixture(out CcdLayout.PATH_INFO[] paths, out CcdLayout.MODE_INFO[] modes)
        {
            var tMain = new CcdLayout.MODE_INFO();
            tMain.infoType = 2; tMain.id = 4357;
            tMain.targetMode.activeSize.cx = 3840; tMain.targetMode.activeSize.cy = 2160;
            tMain.targetMode.vSyncFreq.Numerator = 160; tMain.targetMode.vSyncFreq.Denominator = 1;
            var sMain = SigSource(3840, 2160, 2560, 139); sMain.id = 1;
            var tSec = new CcdLayout.MODE_INFO();
            tSec.infoType = 2; tSec.id = 4355;
            tSec.targetMode.activeSize.cx = 2560; tSec.targetMode.activeSize.cy = 1440;
            var sSec = SigSource(2560, 1440, 0, 0); sSec.id = 0;
            modes = new CcdLayout.MODE_INFO[] { tMain, sMain, tSec, sSec };

            var pMain = SigPath(4357, 1, 160, 1);
            pMain.sourceInfo.id = 1; pMain.sourceInfo.statusFlags = 1;
            pMain.targetInfo.modeInfoIdx = 0; pMain.targetInfo.statusFlags = 1;
            pMain.targetInfo.outputTechnology = 10; pMain.targetInfo.scaling = 1; pMain.targetInfo.targetAvailable = 1;
            var pSec = SigPath(4355, 3, 170001, 1000);
            pSec.sourceInfo.id = 0; pSec.targetInfo.modeInfoIdx = 2;
            paths = new CcdLayout.PATH_INFO[] { pMain, pSec };
        }

        public static void Test_MainOnlyConfig_RemapsIndicesAndMovesToOrigin()
        {
            CcdLayout.PATH_INFO[] snapPaths; CcdLayout.MODE_INFO[] snapModes;
            SnapFixture(out snapPaths, out snapModes);
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            CcdLayout.MainOnlyConfig(snapPaths, snapModes, 0, out paths, out modes);

            Assert.Eq(1, paths.Length, "one path");
            Assert.Eq(2, modes.Length, "two modes");
            CcdLayout.PATH_INFO p = paths[0];
            Assert.Eq((uint)1, p.sourceInfo.id, "main source id kept");
            Assert.Eq((uint)4357, p.targetInfo.id, "main target id");
            Assert.Eq((uint)0, p.sourceInfo.modeInfoIdx, "source mode at 0");
            Assert.Eq((uint)1, p.targetInfo.modeInfoIdx, "target mode at 1");
            Assert.Eq((uint)1, p.flags, "path active");
            Assert.Eq((uint)0, p.sourceInfo.statusFlags, "source status cleared");
            Assert.Eq((uint)0, p.targetInfo.statusFlags, "target status cleared");
            Assert.Eq((uint)160, p.targetInfo.refreshRate.Numerator, "refresh kept");
            Assert.Eq((uint)1, p.targetInfo.scaling, "scaling kept");
            Assert.Eq((uint)10, p.targetInfo.outputTechnology, "output technology kept");

            Assert.Eq((uint)1, modes[0].infoType, "mode 0 is a source mode");
            Assert.Eq((uint)1, modes[0].id, "source mode id");
            Assert.Eq((uint)3840, modes[0].sourceMode.width, "source width");
            Assert.Eq((uint)2160, modes[0].sourceMode.height, "source height");
            Assert.Eq(0, modes[0].sourceMode.x, "moved to x=0");
            Assert.Eq(0, modes[0].sourceMode.y, "moved to y=0");
            Assert.Eq((uint)2, modes[1].infoType, "mode 1 is a target mode");
            Assert.Eq((uint)4357, modes[1].id, "target mode id");
            Assert.Eq((uint)160, modes[1].targetMode.vSyncFreq.Numerator, "target timing kept");

            // 快照数组本身不被改动
            Assert.Eq((uint)1, snapPaths[0].sourceInfo.modeInfoIdx, "snapshot path untouched");
            Assert.Eq(2560, snapModes[1].sourceMode.x, "snapshot mode untouched");
            Assert.Eq("t4357 3840x2160@0,0 r1 160.0Hz", CcdLayout.Signature(paths, modes), "signature of main-only config");
        }

        public static void Test_MainOnlyConfig_MainPathSecond()
        {
            CcdLayout.PATH_INFO[] snapPaths; CcdLayout.MODE_INFO[] snapModes;
            SnapFixture(out snapPaths, out snapModes);
            var swapped = new CcdLayout.PATH_INFO[] { snapPaths[1], snapPaths[0] };
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            CcdLayout.MainOnlyConfig(swapped, snapModes, 1, out paths, out modes);
            Assert.Eq((uint)4357, paths[0].targetInfo.id, "picked the main path");
            Assert.Eq((uint)3840, modes[0].sourceMode.width, "main source mode");
            Assert.Eq((uint)4357, modes[1].id, "main target mode");
        }

        public static void Test_MainOnlyConfig_BadIndicesThrow()
        {
            CcdLayout.PATH_INFO[] snapPaths; CcdLayout.MODE_INFO[] snapModes;
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;

            SnapFixture(out snapPaths, out snapModes);
            snapPaths[0].sourceInfo.modeInfoIdx = 0xFFFFFFFF;
            Assert.Throws<LayoutException>(delegate { CcdLayout.MainOnlyConfig(snapPaths, snapModes, 0, out paths, out modes); }, "source index invalid");

            SnapFixture(out snapPaths, out snapModes);
            snapPaths[0].targetInfo.modeInfoIdx = 4;
            Assert.Throws<LayoutException>(delegate { CcdLayout.MainOnlyConfig(snapPaths, snapModes, 0, out paths, out modes); }, "target index past end");

            SnapFixture(out snapPaths, out snapModes);
            snapPaths[0].targetInfo.modeInfoIdx = 0xFFFFFFFF;
            Assert.Throws<LayoutException>(delegate { CcdLayout.MainOnlyConfig(snapPaths, snapModes, 0, out paths, out modes); }, "target index invalid");

            // 索引指向了错误类型的模式
            SnapFixture(out snapPaths, out snapModes);
            snapPaths[0].sourceInfo.modeInfoIdx = 0;
            Assert.Throws<LayoutException>(delegate { CcdLayout.MainOnlyConfig(snapPaths, snapModes, 0, out paths, out modes); }, "source index at a target mode");

            SnapFixture(out snapPaths, out snapModes);
            snapPaths[0].targetInfo.modeInfoIdx = 1;
            Assert.Throws<LayoutException>(delegate { CcdLayout.MainOnlyConfig(snapPaths, snapModes, 0, out paths, out modes); }, "target index at a source mode");

            SnapFixture(out snapPaths, out snapModes);
            Assert.Throws<LayoutException>(delegate { CcdLayout.MainOnlyConfig(snapPaths, snapModes, 2, out paths, out modes); }, "main index out of range");
        }

        // ---- 仅主屏应用后的拓扑复查：容忍切换输入时短暂的 Missing ----

        // 依次返回给定的拓扑，用完后一直返回最后一个；记录查询次数与累计等待
        class ModeSeq
        {
            readonly LayoutMode[] seq; int i;
            public int Queries, Slept, Sleeps;
            public ModeSeq(params LayoutMode[] seq) { this.seq = seq; }
            public LayoutMode Next() { Queries++; LayoutMode m = seq[Math.Min(i, seq.Length - 1)]; i++; return m; }
            public void Sleep(int ms) { Slept += ms; Sleeps++; }
        }

        static LayoutMode Settle(ModeSeq s, out int waited)
        {
            return CcdLayout.WaitWhileMissing(s.Next, s.Sleep, 3000, 250, out waited);
        }

        public static void Test_WaitWhileMissing_NotMissing_ReturnsAtOnce()
        {
            foreach (LayoutMode m in new LayoutMode[] { LayoutMode.MainOnly, LayoutMode.Extend, LayoutMode.Clone })
            {
                var s = new ModeSeq(m);
                int waited;
                Assert.Eq(m, Settle(s, out waited), "returned " + m);
                Assert.Eq(1, s.Queries, "queried once for " + m);
                Assert.Eq(0, s.Slept, "no wait for " + m);
                Assert.Eq(0, waited, "waited 0 for " + m);
            }
        }

        public static void Test_WaitWhileMissing_BlipThenMainOnly()
        {
            var s = new ModeSeq(LayoutMode.Missing, LayoutMode.Missing, LayoutMode.Missing, LayoutMode.MainOnly);
            int waited;
            Assert.Eq(LayoutMode.MainOnly, Settle(s, out waited), "main only once the blip is over");
            Assert.Eq(4, s.Queries, "re-queried while Missing");
            Assert.Eq(3, s.Sleeps, "slept between queries");
            Assert.Eq(750, s.Slept, "250 ms steps");
            Assert.Eq(750, waited, "reports the wait");
        }

        public static void Test_WaitWhileMissing_BlipThenExtendOrClone_ReturnsIt()
        {
            var s = new ModeSeq(LayoutMode.Missing, LayoutMode.Extend);
            int waited;
            Assert.Eq(LayoutMode.Extend, Settle(s, out waited), "extend is returned (caller throws)");
            Assert.Eq(250, s.Slept, "one step");
            var s2 = new ModeSeq(LayoutMode.Missing, LayoutMode.Missing, LayoutMode.Clone);
            Assert.Eq(LayoutMode.Clone, Settle(s2, out waited), "clone is returned (caller throws)");
            Assert.Eq(500, s2.Slept, "two steps");
        }

        public static void Test_WaitWhileMissing_StillMissingAfterDeadline()
        {
            var s = new ModeSeq(LayoutMode.Missing);
            int waited;
            Assert.Eq(LayoutMode.Missing, Settle(s, out waited), "still Missing after the deadline");
            Assert.Eq(3000, s.Slept, "waited the full 3 s");
            Assert.Eq(3000, waited, "reports the wait");
            Assert.Eq(13, s.Queries, "initial query + one per 250 ms step");
        }

        // ---- 通用小工具 ----
        static CcdLayout.LUID Luid(uint low, int high)
        {
            var l = new CcdLayout.LUID();
            l.LowPart = low; l.HighPart = high;
            return l;
        }

        static void LuidEq(CcdLayout.LUID want, CcdLayout.LUID got, string msg)
        {
            Assert.Eq(want.LowPart + ":" + want.HighPart, got.LowPart + ":" + got.HighPart, msg);
        }

        static void BytesEq(byte[] want, byte[] got, string msg)
        {
            Assert.Eq(BitConverter.ToString(want), BitConverter.ToString(got), msg);
        }

        static byte[] Concat(params byte[][] parts)
        {
            var ms = new System.IO.MemoryStream();
            foreach (byte[] p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        static byte[] Head(byte[] b, int len)
        {
            var r = new byte[len];
            Array.Copy(b, r, len);
            return r;
        }

        static byte[] I32(int v) { return BitConverter.GetBytes(v); }

        const string DevMain = @"\\?\DISPLAY#LHC9059#5&2a9f1c3&0&UID4357#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        const string DevSec = @"\\?\DISPLAY#BOE0A1B#4&1d0e6f2&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        // 两块屏分属两块显卡的快照：主屏路径 0 和它的模式 0、1 在保存时的适配器 A 上，
        // 副屏路径 1 和它的模式 2、3 在适配器 B 上。
        static void TwoAdapterFixture(out CcdLayout.PATH_INFO[] paths, out CcdLayout.MODE_INFO[] modes)
        {
            SnapFixture(out paths, out modes);
            CcdLayout.LUID a = Luid(0x1111, 0), b = Luid(0x2222, 0);
            paths[0].sourceInfo.adapterId = a; paths[0].targetInfo.adapterId = a;
            modes[0].adapterId = a; modes[1].adapterId = a;
            paths[1].sourceInfo.adapterId = b; paths[1].targetInfo.adapterId = b;
            modes[2].adapterId = b; modes[3].adapterId = b;
            paths[1].sourceInfo.statusFlags = 1; paths[1].targetInfo.statusFlags = 0x40001;
            paths[1].targetInfo.outputTechnology = 0x80000000; paths[1].targetInfo.scaling = 2; paths[1].targetInfo.targetAvailable = 1;
        }

        // ---- 让出（克隆）配置 ----

        // 改成纯函数之前 Release() 里的构造办法，原样抄在这里作对照：
        // 副屏的源同时输出到副屏（路径 0）和主屏（路径 1），源模式用给定的分辨率。
        static void ReleaseConfigBefore(CcdLayout.PATH_INFO[] snapPaths, CcdLayout.MODE_INFO[] snapModes, int mainIdx, int w, int h,
            out CcdLayout.PATH_INFO[] paths, out CcdLayout.MODE_INFO[] modes)
        {
            CcdLayout.PATH_INFO pm = snapPaths[mainIdx];
            CcdLayout.PATH_INFO ps = snapPaths[1 - mainIdx];

            modes = new CcdLayout.MODE_INFO[2];
            modes[0] = new CcdLayout.MODE_INFO();
            modes[0].infoType = 1; modes[0].id = ps.sourceInfo.id; modes[0].adapterId = ps.sourceInfo.adapterId;
            modes[0].sourceMode.width = (uint)w; modes[0].sourceMode.height = (uint)h;
            modes[0].sourceMode.pixelFormat = 4; modes[0].sourceMode.x = 0; modes[0].sourceMode.y = 0;
            modes[1] = snapModes[ps.targetInfo.modeInfoIdx];
            modes[1].infoType = 2; modes[1].id = ps.targetInfo.id;

            paths = new CcdLayout.PATH_INFO[2];
            paths[0] = ps;
            paths[0].sourceInfo.modeInfoIdx = 0; paths[0].sourceInfo.statusFlags = 0;
            paths[0].targetInfo.modeInfoIdx = 1; paths[0].targetInfo.statusFlags = 0;
            paths[0].flags = 1;

            paths[1] = new CcdLayout.PATH_INFO();
            paths[1].sourceInfo = ps.sourceInfo; paths[1].sourceInfo.modeInfoIdx = 0; paths[1].sourceInfo.statusFlags = 0;
            paths[1].targetInfo.adapterId = pm.targetInfo.adapterId; paths[1].targetInfo.id = pm.targetInfo.id;
            paths[1].targetInfo.outputTechnology = pm.targetInfo.outputTechnology;
            paths[1].targetInfo.modeInfoIdx = 0xFFFFFFFF;
            paths[1].targetInfo.rotation = 1; paths[1].targetInfo.scaling = 128;
            paths[1].targetInfo.refreshRate.Numerator = 0; paths[1].targetInfo.refreshRate.Denominator = 0;
            paths[1].targetInfo.scanLineOrdering = 0; paths[1].targetInfo.targetAvailable = 1; paths[1].targetInfo.statusFlags = 0;
            paths[1].flags = 1;
        }

        public static void Test_ReleaseConfig_ConfiguredResolution_SameBytesAsBefore()
        {
            for (int mainIdx = 0; mainIdx < 2; mainIdx++)
            {
                CcdLayout.PATH_INFO[] snapPaths; CcdLayout.MODE_INFO[] snapModes;
                TwoAdapterFixture(out snapPaths, out snapModes);
                if (mainIdx == 1) snapPaths = new CcdLayout.PATH_INFO[] { snapPaths[1], snapPaths[0] };
                CcdLayout.PATH_INFO[] wantPaths; CcdLayout.MODE_INFO[] wantModes;
                ReleaseConfigBefore(snapPaths, snapModes, mainIdx, 1920, 1080, out wantPaths, out wantModes);
                CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
                CcdLayout.ReleaseConfig(snapPaths, snapModes, mainIdx, 1920, 1080, out paths, out modes);
                BytesEq(CcdLayout.ToBytes(wantPaths), CcdLayout.ToBytes(paths), "paths, main at " + mainIdx);
                BytesEq(CcdLayout.ToBytes(wantModes), CcdLayout.ToBytes(modes), "modes, main at " + mainIdx);
            }
        }

        public static void Test_ReleaseConfig_OtherScreenSourceFeedsBothTargets()
        {
            CcdLayout.PATH_INFO[] snapPaths; CcdLayout.MODE_INFO[] snapModes;
            SnapFixture(out snapPaths, out snapModes);
            byte[] pathsBefore = CcdLayout.ToBytes(snapPaths), modesBefore = CcdLayout.ToBytes(snapModes);
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 1920, 1080, out paths, out modes);

            Assert.Eq(2, paths.Length, "two paths");
            Assert.Eq(2, modes.Length, "two modes");
            Assert.Eq((uint)4355, paths[0].targetInfo.id, "path 0 goes to the other screen");
            Assert.Eq((uint)4357, paths[1].targetInfo.id, "path 1 goes to the main screen");
            Assert.Eq((uint)0, paths[0].sourceInfo.id, "path 0 uses the other screen's source");
            Assert.Eq((uint)0, paths[1].sourceInfo.id, "path 1 uses the same source");
            Assert.Eq((uint)0, paths[0].sourceInfo.modeInfoIdx, "path 0 source mode");
            Assert.Eq((uint)0, paths[1].sourceInfo.modeInfoIdx, "path 1 source mode");
            Assert.Eq((uint)1, paths[0].targetInfo.modeInfoIdx, "path 0 target mode");
            Assert.Eq(0xFFFFFFFF, paths[1].targetInfo.modeInfoIdx, "main target mode left to the system");
            Assert.Eq((uint)1, paths[0].flags, "path 0 active");
            Assert.Eq((uint)1, paths[1].flags, "path 1 active");
            Assert.Eq((uint)10, paths[1].targetInfo.outputTechnology, "main output technology kept");

            Assert.Eq((uint)1, modes[0].infoType, "mode 0 is a source mode");
            Assert.Eq((uint)1920, modes[0].sourceMode.width, "configured width");
            Assert.Eq((uint)1080, modes[0].sourceMode.height, "configured height");
            Assert.Eq((uint)4, modes[0].sourceMode.pixelFormat, "32 bpp");
            Assert.Eq(0, modes[0].sourceMode.x, "x");
            Assert.Eq(0, modes[0].sourceMode.y, "y");
            Assert.Eq((uint)2, modes[1].infoType, "mode 1 is a target mode");
            Assert.Eq((uint)4355, modes[1].id, "the other screen's target mode");
            Assert.Eq((uint)2560, modes[1].targetMode.activeSize.cx, "target timing kept");

            // 快照数组本身不被改动
            BytesEq(pathsBefore, CcdLayout.ToBytes(snapPaths), "snapshot paths untouched");
            BytesEq(modesBefore, CcdLayout.ToBytes(snapModes), "snapshot modes untouched");
        }

        public static void Test_ReleaseConfig_Zero_UsesOtherScreenResolution()
        {
            CcdLayout.PATH_INFO[] snapPaths; CcdLayout.MODE_INFO[] snapModes;
            TwoAdapterFixture(out snapPaths, out snapModes);
            // 副屏（路径 1）的源模式：1920x1200，摆在主屏左边
            snapModes[3].sourceMode.width = 1920; snapModes[3].sourceMode.height = 1200;
            snapModes[3].sourceMode.x = -1920; snapModes[3].sourceMode.y = 300;

            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 0, 0, out paths, out modes);
            Assert.Eq((uint)1920, modes[0].sourceMode.width, "width from the other screen");
            Assert.Eq((uint)1200, modes[0].sourceMode.height, "height from the other screen");
            Assert.Eq(0, modes[0].sourceMode.x, "clone source at x=0");
            Assert.Eq(0, modes[0].sourceMode.y, "clone source at y=0");

            // 除了分辨率的来源，其余与配置成 1920x1200 完全相同
            CcdLayout.PATH_INFO[] wantPaths; CcdLayout.MODE_INFO[] wantModes;
            CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 1920, 1200, out wantPaths, out wantModes);
            BytesEq(CcdLayout.ToBytes(wantPaths), CcdLayout.ToBytes(paths), "paths");
            BytesEq(CcdLayout.ToBytes(wantModes), CcdLayout.ToBytes(modes), "modes");

            // 宽、高只要有一个不是正数，就取副屏自己的
            CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 0, 1440, out paths, out modes);
            BytesEq(CcdLayout.ToBytes(wantModes), CcdLayout.ToBytes(modes), "width 0");
            CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 2560, 0, out paths, out modes);
            BytesEq(CcdLayout.ToBytes(wantModes), CcdLayout.ToBytes(modes), "height 0");
            CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, -1, -1, out paths, out modes);
            BytesEq(CcdLayout.ToBytes(wantModes), CcdLayout.ToBytes(modes), "negative");

            // 主屏是第二条路径时，取的是第一条（副屏）的
            var swapped = new CcdLayout.PATH_INFO[] { snapPaths[1], snapPaths[0] };
            CcdLayout.ReleaseConfig(swapped, snapModes, 1, 0, 0, out paths, out modes);
            Assert.Eq((uint)1920, modes[0].sourceMode.width, "main second: width");
            Assert.Eq((uint)1200, modes[0].sourceMode.height, "main second: height");
        }

        public static void Test_ReleaseConfig_Zero_OtherScreenWithoutSourceModeThrows()
        {
            CcdLayout.PATH_INFO[] snapPaths; CcdLayout.MODE_INFO[] snapModes;
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;

            SnapFixture(out snapPaths, out snapModes);
            snapPaths[1].sourceInfo.modeInfoIdx = 0xFFFFFFFF;
            Assert.Throws<LayoutException>(delegate { CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 0, 0, out paths, out modes); }, "source index invalid");
            // 配置了分辨率时用不到副屏的源模式，和以前一样能构造
            CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 2560, 1440, out paths, out modes);
            Assert.Eq((uint)2560, modes[0].sourceMode.width, "configured width still used");

            SnapFixture(out snapPaths, out snapModes);
            snapPaths[1].sourceInfo.modeInfoIdx = 4;
            Assert.Throws<LayoutException>(delegate { CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 0, 0, out paths, out modes); }, "source index past end");

            // 索引指向的是目标模式
            SnapFixture(out snapPaths, out snapModes);
            snapPaths[1].sourceInfo.modeInfoIdx = 2;
            Assert.Throws<LayoutException>(delegate { CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 0, 0, out paths, out modes); }, "source index at a target mode");

            SnapFixture(out snapPaths, out snapModes);
            snapModes[3].sourceMode.width = 0;
            Assert.Throws<LayoutException>(delegate { CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 0, 0, out paths, out modes); }, "zero width in the snapshot");

            SnapFixture(out snapPaths, out snapModes);
            snapModes[3].sourceMode.height = 0;
            Assert.Throws<LayoutException>(delegate { CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 0, 0, out paths, out modes); }, "zero height in the snapshot");
        }

        public static void Test_ReleaseConfig_BadSnapshotThrows()
        {
            CcdLayout.PATH_INFO[] snapPaths; CcdLayout.MODE_INFO[] snapModes;
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;

            // 副屏没有目标模式：配不配分辨率都不行
            SnapFixture(out snapPaths, out snapModes);
            snapPaths[1].targetInfo.modeInfoIdx = 0xFFFFFFFF;
            Assert.Throws<LayoutException>(delegate { CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 2560, 1440, out paths, out modes); }, "target index invalid");
            Assert.Throws<LayoutException>(delegate { CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 0, 0, out paths, out modes); }, "target index invalid, release 0");

            SnapFixture(out snapPaths, out snapModes);
            snapPaths[1].targetInfo.modeInfoIdx = 4;
            Assert.Throws<LayoutException>(delegate { CcdLayout.ReleaseConfig(snapPaths, snapModes, 0, 2560, 1440, out paths, out modes); }, "target index past end");

            SnapFixture(out snapPaths, out snapModes);
            Assert.Throws<LayoutException>(delegate { CcdLayout.ReleaseConfig(snapPaths, snapModes, 2, 2560, 1440, out paths, out modes); }, "main index out of range");
            Assert.Throws<LayoutException>(delegate { CcdLayout.ReleaseConfig(snapPaths, snapModes, -1, 2560, 1440, out paths, out modes); }, "main index negative");

            var one = new CcdLayout.PATH_INFO[] { snapPaths[0] };
            Assert.Throws<LayoutException>(delegate { CcdLayout.ReleaseConfig(one, snapModes, 0, 2560, 1440, out paths, out modes); }, "only one path");
        }

        // ---- 按路径替换适配器 LUID ----

        public static void Test_PatchLuids_EachPathGetsItsOwnAdapter()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            TwoAdapterFixture(out paths, out modes);
            string sigBefore = CcdLayout.Signature(paths, modes);
            CcdLayout.LUID igpu = Luid(0xAAAA, 1), dgpu = Luid(0xBBBB, 2), other = Luid(0xDDDD, 4), legacy = Luid(0xCCCC, 3);
            // 当前目标的顺序与快照无关，中间还夹着一个不相干的
            string[] curDevs = { DevSec, @"\\?\DISPLAY#XYZ0001#1&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", DevMain };
            CcdLayout.LUID[] curLuids = { igpu, other, dgpu };

            bool[] found = CcdLayout.PatchLuids(paths, modes, new string[] { DevMain, DevSec }, curDevs, curLuids, legacy);

            Assert.Eq(2, found.Length, "one answer per path");
            Assert.True(found[0] && found[1], "both found");
            LuidEq(dgpu, paths[0].sourceInfo.adapterId, "main source");
            LuidEq(dgpu, paths[0].targetInfo.adapterId, "main target");
            LuidEq(dgpu, modes[0].adapterId, "main target mode");
            LuidEq(dgpu, modes[1].adapterId, "main source mode");
            LuidEq(igpu, paths[1].sourceInfo.adapterId, "other source");
            LuidEq(igpu, paths[1].targetInfo.adapterId, "other target");
            LuidEq(igpu, modes[2].adapterId, "other target mode");
            LuidEq(igpu, modes[3].adapterId, "other source mode");
            Assert.Eq(sigBefore, CcdLayout.Signature(paths, modes), "nothing but the adapters changed");
            Assert.Eq((uint)4357, paths[0].targetInfo.id, "target id kept");
            Assert.Eq((uint)1, paths[0].sourceInfo.modeInfoIdx, "mode index kept");
        }

        // 模式跟着引用它的路径走，而不是按在数组里的位置
        public static void Test_PatchLuids_ModesFollowTheirPath()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            TwoAdapterFixture(out paths, out modes);
            // 路径 0 改为引用模式 2、3，路径 1 引用模式 0、1；再加一个谁也不引用的模式
            paths[0].targetInfo.modeInfoIdx = 2; paths[0].sourceInfo.modeInfoIdx = 3;
            paths[1].targetInfo.modeInfoIdx = 0; paths[1].sourceInfo.modeInfoIdx = 1;
            var extra = new CcdLayout.MODE_INFO[5];
            Array.Copy(modes, extra, 4);
            extra[4] = SigTarget(); extra[4].adapterId = Luid(0x1111, 0);
            CcdLayout.LUID igpu = Luid(0xAAAA, 1), dgpu = Luid(0xBBBB, 2), legacy = Luid(0xCCCC, 3);

            CcdLayout.PatchLuids(paths, extra, new string[] { DevMain, DevSec },
                new string[] { DevMain, DevSec }, new CcdLayout.LUID[] { dgpu, igpu }, legacy);

            LuidEq(dgpu, extra[2].adapterId, "path 0 target mode");
            LuidEq(dgpu, extra[3].adapterId, "path 0 source mode");
            LuidEq(igpu, extra[0].adapterId, "path 1 target mode");
            LuidEq(igpu, extra[1].adapterId, "path 1 source mode");
            LuidEq(legacy, extra[4].adapterId, "unreferenced mode is patched the old way");

            // 无效的模式索引不出错
            TwoAdapterFixture(out paths, out modes);
            paths[1].targetInfo.modeInfoIdx = 0xFFFFFFFF; paths[1].sourceInfo.modeInfoIdx = 9;
            bool[] found = CcdLayout.PatchLuids(paths, modes, new string[] { DevMain, DevSec },
                new string[] { DevMain, DevSec }, new CcdLayout.LUID[] { dgpu, igpu }, legacy);
            Assert.True(found[1], "found despite invalid mode indices");
            LuidEq(igpu, paths[1].targetInfo.adapterId, "path still patched");
        }

        public static void Test_PatchLuids_DevicePathComparedIgnoringCase()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            TwoAdapterFixture(out paths, out modes);
            CcdLayout.LUID igpu = Luid(0xAAAA, 1), dgpu = Luid(0xBBBB, 2), legacy = Luid(0xCCCC, 3);
            bool[] found = CcdLayout.PatchLuids(paths, modes, new string[] { DevMain.ToLowerInvariant(), DevSec.ToUpperInvariant() },
                new string[] { DevMain, DevSec }, new CcdLayout.LUID[] { dgpu, igpu }, legacy);
            Assert.True(found[0] && found[1], "both found");
            LuidEq(dgpu, paths[0].targetInfo.adapterId, "main");
            LuidEq(igpu, paths[1].targetInfo.adapterId, "other");
        }

        public static void Test_PatchLuids_TargetNotFound_FallsBackAndReports()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            TwoAdapterFixture(out paths, out modes);
            CcdLayout.LUID dgpu = Luid(0xBBBB, 2), legacy = Luid(0xCCCC, 3);
            // 副屏现在不在当前路径里
            bool[] found = CcdLayout.PatchLuids(paths, modes, new string[] { DevMain, DevSec },
                new string[] { DevMain }, new CcdLayout.LUID[] { dgpu }, legacy);

            Assert.True(found[0], "main found");
            Assert.True(!found[1], "other reported as not found");
            LuidEq(dgpu, paths[0].sourceInfo.adapterId, "main source");
            LuidEq(dgpu, modes[1].adapterId, "main source mode");
            LuidEq(legacy, paths[1].sourceInfo.adapterId, "other source falls back");
            LuidEq(legacy, paths[1].targetInfo.adapterId, "other target falls back");
            LuidEq(legacy, modes[2].adapterId, "other target mode falls back");
            LuidEq(legacy, modes[3].adapterId, "other source mode falls back");

            // 保存时没取到设备路径（空串）的那条不去匹配，即使当前也有空串
            TwoAdapterFixture(out paths, out modes);
            found = CcdLayout.PatchLuids(paths, modes, new string[] { DevMain, "" },
                new string[] { "", DevMain }, new CcdLayout.LUID[] { Luid(0xEEEE, 5), dgpu }, legacy);
            Assert.True(found[0] && !found[1], "empty stored path is not matched");
            LuidEq(legacy, paths[1].targetInfo.adapterId, "empty stored path falls back");
        }

        // 旧快照没有设备路径：全部换成第一条活动路径的适配器，和以前一样
        public static void Test_PatchLuids_OldSnapshot_EverythingGetsTheLegacyAdapter()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            TwoAdapterFixture(out paths, out modes);
            CcdLayout.LUID legacy = Luid(0xCCCC, 3);
            bool[] found = CcdLayout.PatchLuids(paths, modes, null, null, null, legacy);
            Assert.Eq(2, found.Length, "one answer per path");
            Assert.True(!found[0] && !found[1], "nothing matched by device path");
            for (int i = 0; i < paths.Length; i++)
            {
                LuidEq(legacy, paths[i].sourceInfo.adapterId, "path " + i + " source");
                LuidEq(legacy, paths[i].targetInfo.adapterId, "path " + i + " target");
            }
            for (int i = 0; i < modes.Length; i++) LuidEq(legacy, modes[i].adapterId, "mode " + i);
        }

        public static void Test_IsMainDevicePath()
        {
            Assert.True(CcdLayout.IsMainDevicePath(DevMain, "LHC9059"), "main");
            Assert.True(CcdLayout.IsMainDevicePath(DevMain, "lhc9059"), "case-insensitive");
            Assert.True(!CcdLayout.IsMainDevicePath(DevSec, "LHC9059"), "other screen");
            Assert.True(!CcdLayout.IsMainDevicePath("", "LHC9059"), "empty path");
            Assert.True(!CcdLayout.IsMainDevicePath(null, "LHC9059"), "no path");
        }

        // ---- 快照文件格式 ----

        // 旧格式：int32 路径数，int32 模式数，各路径，各模式，到此为止
        static byte[] OldSnapshotBytes(CcdLayout.PATH_INFO[] paths, CcdLayout.MODE_INFO[] modes)
        {
            return Concat(I32(paths.Length), I32(modes.Length), CcdLayout.ToBytes(paths), CcdLayout.ToBytes(modes));
        }

        static byte[] Ascii(string s) { return System.Text.Encoding.ASCII.GetBytes(s); }

        public static void Test_Snapshot_OldFormat_LoadsWithoutDevicePaths()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            TwoAdapterFixture(out paths, out modes);
            byte[] old = OldSnapshotBytes(paths, modes);
            Assert.Eq(408, old.Length, "2 paths + 4 modes in the old format");

            CcdLayout.PATH_INFO[] p; CcdLayout.MODE_INFO[] m; string[] devs;
            CcdLayout.SnapshotFromBytes(old, out p, out m, out devs);
            Assert.True(devs == null, "an old snapshot has no device paths");
            BytesEq(CcdLayout.ToBytes(paths), CcdLayout.ToBytes(p), "paths");
            BytesEq(CcdLayout.ToBytes(modes), CcdLayout.ToBytes(m), "modes");
            Assert.Eq(CcdLayout.Signature(paths, modes), CcdLayout.Signature(p, m), "signature");

            // 不给设备路径时写出来的就是旧格式
            BytesEq(old, CcdLayout.SnapshotToBytes(paths, modes, null), "no device paths = old format");
        }

        public static void Test_Snapshot_NewFormat_RoundTrips()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            TwoAdapterFixture(out paths, out modes);
            string[] saved = { DevMain, "\\\\?\\DISPLAY#显示器 é#UID4355" };
            byte[] data = CcdLayout.SnapshotToBytes(paths, modes, saved);
            // 前面与旧格式完全相同，设备路径接在后面
            Assert.True(data.Length > 408, "longer than the old format");
            BytesEq(OldSnapshotBytes(paths, modes), Head(data, 408), "old part unchanged");

            CcdLayout.PATH_INFO[] p; CcdLayout.MODE_INFO[] m; string[] devs;
            CcdLayout.SnapshotFromBytes(data, out p, out m, out devs);
            Assert.True(devs != null, "device paths present");
            Assert.Eq(2, devs.Length, "one per path");
            Assert.Eq(saved[0], devs[0], "device path 0");
            Assert.Eq(saved[1], devs[1], "device path 1 (non-ASCII)");
            BytesEq(CcdLayout.ToBytes(paths), CcdLayout.ToBytes(p), "paths");
            BytesEq(CcdLayout.ToBytes(modes), CcdLayout.ToBytes(m), "modes");
            Assert.Eq(CcdLayout.Signature(paths, modes), CcdLayout.Signature(p, m), "signature");

            // 空串（保存时没取到）和 null 都按空串往返
            data = CcdLayout.SnapshotToBytes(paths, modes, new string[] { "", null });
            CcdLayout.SnapshotFromBytes(data, out p, out m, out devs);
            Assert.Eq("", devs[0], "empty");
            Assert.Eq("", devs[1], "null written as empty");
        }

        // 把尾部的字节布局钉住：标记 "TDP1"，int32 条数，每条 int32 字节数 + UTF-8
        public static void Test_Snapshot_NewFormat_TrailerBytes()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            TwoAdapterFixture(out paths, out modes);
            byte[] want = Concat(OldSnapshotBytes(paths, modes), Ascii("TDP1"), I32(2),
                I32(2), Ascii("AB"), I32(2), new byte[] { 0xC3, 0xA9 });
            BytesEq(want, CcdLayout.SnapshotToBytes(paths, modes, new string[] { "AB", "\u00E9" }), "trailer layout");
        }

        static void Corrupt(byte[] data, string what)
        {
            CcdLayout.PATH_INFO[] p; CcdLayout.MODE_INFO[] m; string[] devs;
            try { CcdLayout.SnapshotFromBytes(data, out p, out m, out devs); }
            catch (LayoutException e)
            {
                Assert.True(e.Message.IndexOf("损坏") >= 0, what + ": wrong message: " + e.Message);
                return;
            }
            throw new Exception("no LayoutException: " + what);
        }

        public static void Test_Snapshot_CorruptThrows()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            TwoAdapterFixture(out paths, out modes);
            byte[] old = OldSnapshotBytes(paths, modes);
            byte[] good = CcdLayout.SnapshotToBytes(paths, modes, new string[] { DevMain, DevSec });

            Corrupt(null, "no data");
            // 任何截断都算损坏，除非正好截成一个完整的旧快照
            for (int len = 0; len < good.Length; len++)
                if (len != old.Length) Corrupt(Head(good, len), "truncated to " + len + " bytes");

            // 多出来的字节
            Corrupt(Concat(good, new byte[] { 0 }), "a byte after the trailer");
            Corrupt(Concat(old, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "garbage after an old snapshot");

            // 标记不对
            byte[] bad = (byte[])good.Clone();
            bad[old.Length] ^= 0xFF;
            Corrupt(bad, "bad magic");

            // 条数与路径数不一致
            Corrupt(Concat(old, Ascii("TDP1"), I32(1), I32(1), Ascii("A")), "one device path for two paths");
            Corrupt(Concat(old, Ascii("TDP1"), I32(3), I32(1), Ascii("A"), I32(1), Ascii("B"), I32(1), Ascii("C")), "three device paths for two paths");

            // 长度不合理
            Corrupt(Concat(old, Ascii("TDP1"), I32(2), I32(-1), I32(1), Ascii("B")), "negative length");
            Corrupt(Concat(old, Ascii("TDP1"), I32(2), I32(5000), new byte[5000], I32(1), Ascii("B")), "absurd length");

            // 不是合法的 UTF-8
            Corrupt(Concat(old, Ascii("TDP1"), I32(2), I32(2), new byte[] { 0xFF, 0xFE }, I32(1), Ascii("B")), "invalid UTF-8");

            // 头部：路径数必须是 2，模式数 1–64
            Corrupt(Concat(I32(1), I32(4), new byte[72 + 4 * 64]), "one path");
            Corrupt(Concat(I32(3), I32(4), new byte[3 * 72 + 4 * 64]), "three paths");
            Corrupt(Concat(I32(2), I32(0), new byte[2 * 72]), "no modes");
            Corrupt(Concat(I32(2), I32(65), new byte[2 * 72 + 65 * 64]), "too many modes");
            Corrupt(Concat(I32(-2), I32(4), new byte[400]), "negative path count");
        }

        // SnapshotSignature 只读文件、不碰硬件：新旧两种文件读出同一个签名，缺失或损坏为 null
        public static void Test_SnapshotSignature_ReadsOldAndNewFiles()
        {
            CcdLayout.PATH_INFO[] paths; CcdLayout.MODE_INFO[] modes;
            TwoAdapterFixture(out paths, out modes);
            string sig = CcdLayout.Signature(paths, modes);
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ss-layout-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string snap = System.IO.Path.Combine(dir, "layout-owned.bin");
                var layout = new CcdLayout(snap, new Config(), new Log(System.IO.Path.Combine(dir, "t.log")));
                Assert.True(!layout.HasSnapshot, "no file yet");
                Assert.True(layout.SnapshotSignature() == null, "missing snapshot");

                System.IO.File.WriteAllBytes(snap, OldSnapshotBytes(paths, modes));
                Assert.Eq(sig, layout.SnapshotSignature(), "old file");

                System.IO.File.WriteAllBytes(snap, CcdLayout.SnapshotToBytes(paths, modes, new string[] { DevMain, DevSec }));
                Assert.Eq(sig, layout.SnapshotSignature(), "new file");

                System.IO.File.WriteAllBytes(snap, Concat(OldSnapshotBytes(paths, modes), new byte[] { 0 }));
                Assert.True(layout.SnapshotSignature() == null, "corrupt file");

                System.IO.File.WriteAllBytes(snap, new byte[100000]);
                Assert.True(layout.SnapshotSignature() == null, "oversized file");
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }
    }
}
