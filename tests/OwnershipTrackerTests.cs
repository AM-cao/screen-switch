namespace ScreenSwitch.Tests
{
    public static class OwnershipTrackerTests
    {
        public static void Test_TakeAfterTwoOk()
        {
            var t = new OwnershipTracker(2, false);
            Assert.Eq(Transition.None, t.Feed(true), "first ok");
            Assert.Eq(Transition.Take, t.Feed(true), "second ok");
            Assert.Eq(true, t.Owned, "owned now");
            Assert.Eq(Transition.None, t.Feed(true), "stays");
        }

        public static void Test_ReleaseAfterTwoFail()
        {
            var t = new OwnershipTracker(2, true);
            Assert.Eq(Transition.None, t.Feed(false), "first fail");
            Assert.Eq(Transition.Release, t.Feed(false), "second fail");
            Assert.Eq(false, t.Owned, "released");
        }

        public static void Test_GlitchDoesNotFlip()
        {
            var t = new OwnershipTracker(2, true);
            Assert.Eq(Transition.None, t.Feed(false), "glitch");
            Assert.Eq(Transition.None, t.Feed(true), "recovered");
            Assert.Eq(Transition.None, t.Feed(false), "glitch again");
            Assert.Eq(true, t.Owned, "still owned");
        }

        public static void Test_ResetClearsStreaks()
        {
            var t = new OwnershipTracker(2, true);
            t.Feed(false);
            t.Reset(false);
            Assert.Eq(false, t.Owned, "reset to not owned");
            Assert.Eq(Transition.None, t.Feed(true), "one ok after reset");
            Assert.Eq(Transition.Take, t.Feed(true), "two ok");
        }
    }
}
