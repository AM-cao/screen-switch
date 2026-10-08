namespace ScreenSwitch.Tests
{
    // 判断两条活动路径是不是克隆（CcdLayout.IsClone，纯函数）
    public static class CloneDetectTests
    {
        static CcdLayout.PATH_INFO Path(uint adapterLow, uint sourceId, uint srcIdx)
        {
            var p = new CcdLayout.PATH_INFO();
            p.sourceInfo.adapterId.LowPart = adapterLow; p.targetInfo.adapterId.LowPart = adapterLow;
            p.sourceInfo.id = sourceId;
            p.sourceInfo.modeInfoIdx = srcIdx;
            p.flags = 1;
            return p;
        }

        static CcdLayout.MODE_INFO Source(int x, int y)
        {
            var m = new CcdLayout.MODE_INFO();
            m.infoType = 1;
            m.sourceMode.width = 2560; m.sourceMode.height = 1440; m.sourceMode.x = x; m.sourceMode.y = y;
            return m;
        }

        public static void Test_SameSourceOnOneAdapter_IsClone()
        {
            var modes = new CcdLayout.MODE_INFO[] { Source(0, 0) };
            Assert.Eq(true, CcdLayout.IsClone(Path(7, 1, 0), Path(7, 1, 0), modes), "one source feeding both targets");
        }

        public static void Test_TwoSourcesSideBySide_IsExtend()
        {
            var modes = new CcdLayout.MODE_INFO[] { Source(0, 0), Source(-2560, 360) };
            Assert.Eq(false, CcdLayout.IsClone(Path(7, 0, 0), Path(7, 1, 1), modes), "one adapter, two sources");
        }

        // 两块显卡：两边的源编号都是 0，不能只比编号
        public static void Test_TwoAdapters_SameSourceId_DifferentPositions_IsExtend()
        {
            var modes = new CcdLayout.MODE_INFO[] { Source(0, 0), Source(2560, 0) };
            Assert.Eq(false, CcdLayout.IsClone(Path(7, 0, 0), Path(9, 0, 1), modes), "extended across two adapters");
        }

        public static void Test_TwoAdapters_SamePosition_IsClone()
        {
            var modes = new CcdLayout.MODE_INFO[] { Source(0, 0), Source(0, 0) };
            Assert.Eq(true, CcdLayout.IsClone(Path(7, 0, 0), Path(9, 0, 1), modes), "duplicated across two adapters");
        }

        public static void Test_MissingSourceModes_NotClone()
        {
            var modes = new CcdLayout.MODE_INFO[] { Source(0, 0) };
            Assert.Eq(false, CcdLayout.IsClone(Path(7, 0, 0), Path(9, 0, 0xFFFFFFFF), modes), "invalid index");
            Assert.Eq(false, CcdLayout.IsClone(Path(7, 0, 0), Path(9, 0, 5), modes), "index out of range");
            Assert.Eq(false, CcdLayout.IsClone(Path(7, 0, 0), Path(9, 0, 0), null), "no modes");
        }
    }
}
