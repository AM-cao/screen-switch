using System.Collections.Generic;

namespace ScreenSwitch.Tests
{
    public static class DetectionTests
    {
        const string Main = "\\\\?\\DISPLAY#LHC9059#5&26941f8e&0&UID4357#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        const string Second = "\\\\?\\DISPLAY#TRG1002#5&26941f8e&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        static List<SetupMonitor> Mons(bool secondAnswers)
        {
            return new List<SetupMonitor> { new SetupMonitor(Main, "P275MV", true, 0x10), new SetupMonitor(Second, "LQ25F165L", secondAnswers, 7) };
        }

        public static void Test_OneAnswering_PickedAutomatically()
        {
            DetectResult r = Detection.Choose(Mons(false), 2, null);
            Assert.Eq(null, r.Error, "ok");
            Assert.Eq("LHC9059", r.MainEdid, "model id");
            Assert.Eq((uint)16, r.SelfCode, "own code");
            Assert.Eq("second", r.Layout, "two screens");
            Assert.Eq("LQ25F165L", r.OtherScreen, "other screen's name");
        }

        public static void Test_SeveralAnswering_NeedsChoice_ThenModelPicks()
        {
            DetectResult r = Detection.Choose(Mons(true), 2, null);
            Assert.True(r.Error != null, "must choose");
            Assert.Eq(2, r.Candidates.Count, "both listed");
            r = Detection.Choose(Mons(true), 2, "lhc9059");
            Assert.Eq(null, r.Error, "chosen by model, case-insensitive");
            Assert.Eq((uint)16, r.SelfCode, "own code from the chosen one");
            r = Detection.Choose(Mons(true), 2, "XYZ0001");
            Assert.True(r.Error != null, "unknown model");
        }

        public static void Test_NoneAnswering_ThreeScreens_OneScreen()
        {
            var none = new List<SetupMonitor> { new SetupMonitor(Main, "P275MV", false, 0) };
            Assert.True(Detection.Choose(none, 1, null).Error != null, "no DDC");
            Assert.True(Detection.Choose(Mons(false), 3, null).Error != null, "three screens");
            var one = new List<SetupMonitor> { new SetupMonitor(Main, "P275MV", true, 0x10) };
            DetectResult r = Detection.Choose(one, 1, null);
            Assert.Eq("none", r.Layout, "only the main screen");
            Assert.Eq("", r.OtherScreen, "no other screen");
        }
    }
}
