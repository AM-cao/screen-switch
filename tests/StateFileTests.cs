using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    public static class StateFileTests
    {
        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            return d;
        }

        public static void Test_State_RoundTrip()
        {
            string d = TempDir();
            StateFile.WriteState(d, true, 1758500000);
            Assert.Eq("owned=1 1758500000", File.ReadAllText(Path.Combine(d, "state.txt")).Trim(), "format");
            bool owned; long ts;
            Assert.True(StateFile.ReadState(d, out owned, out ts), "read ok");
            Assert.Eq(true, owned, "owned");
            Assert.Eq(1758500000L, ts, "ts");
            StateFile.WriteState(d, false, 5);
            Assert.True(StateFile.ReadState(d, out owned, out ts), "read ok 2");
            Assert.Eq(false, owned, "owned 2");
            Directory.Delete(d, true);
        }

        public static void Test_State_MissingReturnsFalse()
        {
            string d = TempDir();
            bool owned; long ts;
            Assert.Eq(false, StateFile.ReadState(d, out owned, out ts), "missing");
            Directory.Delete(d, true);
        }

        public static void Test_Command_ReadDeletes()
        {
            string d = TempDir();
            Assert.Eq(null, StateFile.ReadAndDeleteCommand(d), "none");
            StateFile.WriteCommand(d, "give mac");
            Assert.Eq("give mac", StateFile.ReadAndDeleteCommand(d), "read");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "deleted");
            Assert.Eq(null, StateFile.ReadAndDeleteCommand(d), "none again");
            Directory.Delete(d, true);
        }
    }
}
