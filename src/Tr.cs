using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace ScreenSwitch
{
    // 给人看的文字（docs/settings-ui-design.md 第 8 节）。i18n/strings.tsv 在构建时作为资源 strings.tsv 嵌入。
    // 语言跟随系统界面语言：zh 开头用中文，否则英文。缺的编号原样显示编号，测试会查出来。
    public static class Tr
    {
        static Dictionary<string, string[]> table;
        static string lang;

        public static string Lang
        {
            get { if (lang == null) lang = Detect(CultureInfo.CurrentUICulture.Name); return lang; }
        }

        public static void Use(string l) { lang = l == "zh" ? "zh" : "en"; }

        public static string Detect(string cultureName)
        {
            return cultureName != null && cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
        }

        public static string T(string key, params object[] args)
        {
            string[] row;
            if (!All().TryGetValue(key, out row)) return key;
            string s = Lang == "zh" ? row[0] : row[1];
            return args == null || args.Length == 0 ? s : string.Format(CultureInfo.InvariantCulture, s, args);
        }

        public static bool Has(string key) { return All().ContainsKey(key); }

        public static Dictionary<string, string[]> All()
        {
            if (table == null) table = Parse(ReadResource());
            return table;
        }

        static string ReadResource()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("strings.tsv"))
            {
                if (s == null) return "";
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        public static Dictionary<string, string[]> Parse(string tsv)
        {
            var d = new Dictionary<string, string[]>();
            int n = 0;
            foreach (string raw in tsv.Replace("\r\n", "\n").Split('\n'))
            {
                n++;
                if (raw.Length == 0 || raw.StartsWith("#")) continue;
                string[] f = raw.Split('\t');
                if (f.Length != 3 || f[0].Length == 0 || f[1].Length == 0 || f[2].Length == 0)
                    throw new FormatException("strings.tsv line " + n + ": need key, zh and en separated by tabs");
                if (d.ContainsKey(f[0])) throw new FormatException("strings.tsv line " + n + ": duplicate key " + f[0]);
                d[f[0]] = new string[] { f[1].Replace("\\n", "\n"), f[2].Replace("\\n", "\n") };
            }
            return d;
        }
    }
}
