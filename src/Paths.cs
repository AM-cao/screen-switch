using System;
using System.IO;

namespace ScreenSwitch
{
    public static class Paths
    {
        public static string Dir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string Config { get { return Path.Combine(Dir, "config.json"); } }
        public static string Log { get { return Path.Combine(Dir, "screenswitch.log"); } }
        public static string Snapshot { get { return Path.Combine(Dir, "layout-owned.bin"); } }
    }
}
