using System;
using System.IO;

namespace ScreenSwitch
{
    public static class StateFile
    {
        public static string StatePath(string dir) { return Path.Combine(dir, "state.txt"); }
        public static string CmdPath(string dir) { return Path.Combine(dir, "cmd.txt"); }

        public static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public static void WriteState(string dir, bool owned, long unix)
        {
            string tmp = StatePath(dir) + ".tmp";
            File.WriteAllText(tmp, "owned=" + (owned ? "1" : "0") + " " + unix);
            File.Copy(tmp, StatePath(dir), true);
            File.Delete(tmp);
        }

        public static bool ReadState(string dir, out bool owned, out long unix)
        {
            owned = false; unix = 0;
            string p = StatePath(dir);
            if (!File.Exists(p)) return false;
            string text;
            try { text = File.ReadAllText(p).Trim(); }
            catch (IOException) { try { text = File.ReadAllText(p).Trim(); } catch (IOException) { return false; } }
            string[] parts = text.Split(' ');
            if (parts.Length != 2 || !parts[0].StartsWith("owned=")) return false;
            owned = parts[0] == "owned=1";
            return long.TryParse(parts[1], out unix);
        }

        public static void WriteCommand(string dir, string line)
        {
            string tmp = CmdPath(dir) + ".tmp";
            File.WriteAllText(tmp, line);
            File.Copy(tmp, CmdPath(dir), true);
            File.Delete(tmp);
        }

        public static string ReadAndDeleteCommand(string dir)
        {
            string p = CmdPath(dir);
            if (!File.Exists(p)) return null;
            try
            {
                string s = File.ReadAllText(p).Trim();
                File.Delete(p);
                return s;
            }
            catch (IOException) { return null; }
        }
    }
}
