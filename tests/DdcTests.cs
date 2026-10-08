using System.Collections.Generic;

namespace ScreenSwitch.Tests
{
    public static class DdcTests
    {
        static List<string> Ids(params string[] ids) { return new List<string>(ids); }
        static List<uint> Flags(params uint[] flags) { return new List<uint>(flags); }

        // 扩展：该逻辑显示器下只枚举出主屏自己，且已激活 -> 物理下标 0。
        public static void Test_PhysicalIndex_ExtendSingleActiveMonitor_ReturnsZero()
        {
            int idx = DxvaDdc.PhysicalIndex(Ids("MONITOR\\LHC9059\\..."), Flags(0x3), "LHC9059");
            Assert.Eq(0, idx, "single active monitor is index 0");
        }

        // 仅主屏：TRG 已附加未激活（0x2），LHC 已激活（0x3）-> 激活项里主屏排第 0。
        public static void Test_PhysicalIndex_MainOnly_InactiveBeforeMain_ReturnsZero()
        {
            int idx = DxvaDdc.PhysicalIndex(
                Ids("MONITOR\\TRG1002\\...", "MONITOR\\LHC9059\\..."),
                Flags(0x2, 0x3),
                "LHC9059");
            Assert.Eq(0, idx, "inactive entries must not count toward the physical index");
        }

        // 克隆：两个都激活，主屏在后一项 -> 激活序号 1。
        public static void Test_PhysicalIndex_Clone_MainSecond_ReturnsOne()
        {
            int idx = DxvaDdc.PhysicalIndex(
                Ids("MONITOR\\TRG1002\\...", "MONITOR\\LHC9059\\..."),
                Flags(0x3, 0x3),
                "LHC9059");
            Assert.Eq(1, idx, "main is the second active entry");
        }

        // 克隆：两个都激活，主屏在前一项 -> 激活序号 0。
        public static void Test_PhysicalIndex_Clone_MainFirst_ReturnsZero()
        {
            int idx = DxvaDdc.PhysicalIndex(
                Ids("MONITOR\\LHC9059\\...", "MONITOR\\TRG1002\\..."),
                Flags(0x3, 0x3),
                "LHC9059");
            Assert.Eq(0, idx, "main is the first active entry");
        }

        // 主屏本身未激活 -> -1（即使它是列表里唯一一项）。
        public static void Test_PhysicalIndex_MainInactive_ReturnsMinusOne()
        {
            int idx = DxvaDdc.PhysicalIndex(Ids("MONITOR\\LHC9059\\..."), Flags(0x2), "LHC9059");
            Assert.Eq(-1, idx, "inactive main monitor cannot be located");
        }

        // 找不到匹配的 EDID -> -1。
        public static void Test_PhysicalIndex_NotFound_ReturnsMinusOne()
        {
            int idx = DxvaDdc.PhysicalIndex(
                Ids("MONITOR\\TRG1002\\...", "MONITOR\\ABC1234\\..."),
                Flags(0x3, 0x3),
                "LHC9059");
            Assert.Eq(-1, idx, "no matching EDID");
        }

        // 主屏先列出一条未激活项、后面才是激活项：不能在第一条匹配处就放弃，要继续找到激活的那条。
        public static void Test_PhysicalIndex_InactiveMainEntryBeforeActiveOne_KeepsScanning()
        {
            int idx = DxvaDdc.PhysicalIndex(
                Ids("MONITOR\\LHC9059\\...", "MONITOR\\LHC9059\\..."),
                Flags(0x2, 0x3),
                "LHC9059");
            Assert.Eq(0, idx, "inactive main entry skipped, the active one is index 0");

            idx = DxvaDdc.PhysicalIndex(
                Ids("MONITOR\\TRG1002\\...", "MONITOR\\LHC9059\\...", "MONITOR\\LHC9059\\..."),
                Flags(0x3, 0x2, 0x3),
                "LHC9059");
            Assert.Eq(1, idx, "active main entry counted after the active secondary");
        }

        // EDID 匹配大小写不敏感。
        public static void Test_PhysicalIndex_EdidMatchIsCaseInsensitive()
        {
            int idx = DxvaDdc.PhysicalIndex(
                Ids("MONITOR\\TRG1002\\...", "MONITOR\\lhc9059\\..."),
                Flags(0x2, 0x3),
                "LHC9059");
            Assert.Eq(0, idx, "EDID match ignores case");
        }

        static string Codes(string caps)
        {
            var parts = new List<string>();
            foreach (uint v in DxvaDdc.ParseInputCodes(caps)) parts.Add(v.ToString());
            return string.Join(",", parts.ToArray());
        }

        // capabilities 字符串里 VCP 0x60 支持的取值：十六进制，前面的特性可以带自己的括号
        public static void Test_ParseInputCodes_RealString()
        {
            string caps = "(prot(monitor)type(lcd)model(X)cmds(01 02 03 07 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08 0B) 16 18 1A 52 60(0F 10 11 12) AC AE B2 B6 C6 C8 C9 D6(01 04) DF)mswhql(1)asset_eep(40)mccs_ver(2.1))";
            Assert.Eq("15,16,17,18", Codes(caps), "0F 10 11 12");
        }

        // 真机（P275MV）读到的字符串：型号段不规范，特性之间有的没有空格，取值前面带空格
        public static void Test_ParseInputCodes_StringReadFromTheRealMonitor()
        {
            string caps = "(prot(monitor)type(lcd)MStarcmds(01 02 03 07 0C E3 F3)vcp(02 04 05 08 10 12 14(05 06 08 0B 0C 0D)16 18 1A 60( 11 12 0F 10) AA(01 02) 62 AC AE B2 B6 C6 C8 C9 CC(01 02 03 04 05 06 07 08 09 0A 0C 0D 0E 0F 14 16 17 19 1E 23 24 25)D6(01 04 05)  DF FD E0 E1 E2 E3 F0 F3 F7 FE FF)mccs_ver(2.1)mswhql(1))";
            Assert.Eq("17,18,15,16", Codes(caps), "in the order the monitor lists them");
        }

        public static void Test_ParseInputCodes_AbsentOrWithoutValues_Empty()
        {
            Assert.Eq("", Codes("(prot(monitor)vcp(02 04 10 12 14(05 08 0B) D6(01 04))mccs_ver(2.1))"), "no 0x60");
            Assert.Eq("", Codes("(prot(monitor)vcp(02 10 60 AC D6(01 04))mccs_ver(2.1))"), "0x60 without a value list");
            Assert.Eq("", Codes("(prot(monitor)vcp(02 10 60() AC))"), "empty value list");
            Assert.Eq("", Codes("(prot(monitor)type(lcd)model(X))"), "no vcp section");
            Assert.Eq("", Codes(""), "empty");
            Assert.Eq("", Codes(null), "null");
            Assert.Eq("", Codes("garbage 60(0F 10)"), "not inside a vcp section");
        }

        // 0x60 只认作特性编码，不认别的特性的取值；值列表外的 60 也不算
        public static void Test_ParseInputCodes_OnlyTheFeatureItself()
        {
            Assert.Eq("17,18", Codes("(vcp(14(60 0F) 60(11 12) D6(60(01)))"), "60 inside another feature's values ignored");
            Assert.Eq("15", Codes("(vcpname(60(Input))vcp(60(0F)))"), "vcpname is a different section");
        }

        public static void Test_ParseInputCodes_Variants()
        {
            Assert.Eq("15,17", Codes("(PROT(monitor)VCP(10 60(0f 11)))"), "case-insensitive");
            Assert.Eq("15,16,17", Codes("(vcp(60(0F1011)))"), "values written without spaces");
            Assert.Eq("15,17", Codes("(vcp( 60 ( 0F  11 ) ))"), "extra spaces");
            Assert.Eq("15,17", Codes("(vcp(10 60(0F 11"), "truncated string");
            Assert.Eq("15,17", Codes("(vcp(60(0F 11 0F 00 zz)))"), "duplicates, 0 and non-hex dropped");
            Assert.Eq("27", Codes("vcp(60(1B))"), "no outer parentheses");
        }
    }
}
