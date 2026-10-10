using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ScreenSwitch.Tests
{
    public static class TrTests
    {
        public static void Test_Parse_SkipsCommentsAndBlankLines()
        {
            var d = Tr.Parse("# c\n\na\t甲\tA\r\nb\t乙 {0}\tB {0}\n");
            Assert.Eq(2, d.Count, "two rows");
            Assert.Eq("甲", d["a"][0], "zh");
            Assert.Eq("B {0}", d["b"][1], "en");
        }

        public static void Test_Parse_RejectsBadRows()
        {
            Assert.Throws<FormatException>(delegate { Tr.Parse("a\t甲\n"); }, "missing column");
            Assert.Throws<FormatException>(delegate { Tr.Parse("a\t\tA\n"); }, "empty zh");
            Assert.Throws<FormatException>(delegate { Tr.Parse("a\t甲\tA\na\t乙\tB\n"); }, "duplicate key");
        }

        public static void Test_Parse_NewlineEscape()
        {
            Assert.Eq("一\n二", Tr.Parse("k\t一\\n二\tone\\ntwo")["k"][0], "\\n becomes a newline");
        }

        public static void Test_Detect_FollowsUiLanguage()
        {
            Assert.Eq("zh", Tr.Detect("zh-CN"), "zh-CN");
            Assert.Eq("zh", Tr.Detect("zh-Hant-TW"), "zh-Hant");
            Assert.Eq("en", Tr.Detect("en-US"), "en-US");
            Assert.Eq("en", Tr.Detect("ja-JP"), "other languages -> English");
            Assert.Eq("en", Tr.Detect(""), "invariant -> English");
        }

        // 界面语言设置（config.json 的 uiLanguage）：auto | zh | en，缺失或不认识的值算 auto
        public static void Test_LanguageSetting_Normalize()
        {
            Assert.Eq("auto", Tr.Setting(null), "missing");
            Assert.Eq("auto", Tr.Setting(""), "empty");
            Assert.Eq("auto", Tr.Setting("fr"), "unknown");
            Assert.Eq("zh", Tr.Setting("zh"), "zh");
            Assert.Eq("en", Tr.Setting(" EN "), "case and spaces");
            Assert.Eq("auto", Tr.Setting("auto"), "auto");
        }

        // 设置对应到实际使用的语言：auto 照旧跟随系统界面语言
        public static void Test_LanguageSetting_Resolve()
        {
            Assert.Eq("zh", Tr.Resolve("zh", "en-US"), "zh wins over an English system");
            Assert.Eq("en", Tr.Resolve("en", "zh-CN"), "en wins over a Chinese system");
            Assert.Eq("zh", Tr.Resolve("auto", "zh-TW"), "auto on a Chinese system");
            Assert.Eq("en", Tr.Resolve("auto", "ja-JP"), "auto on another system");
            Assert.Eq("zh", Tr.Resolve(null, "zh-CN"), "missing = auto");
            Assert.Eq("en", Tr.Resolve("bogus", "en-US"), "unknown = auto");
        }

        public static void Test_LanguageSetting_Apply()
        {
            try
            {
                Tr.Apply("en");
                Assert.Eq("en", Tr.Lang, "en applied");
                Assert.Eq("Done.", Tr.T("cmd.ok"), "text follows");
                Tr.Apply("zh");
                Assert.Eq("zh", Tr.Lang, "zh applied");
                Tr.Apply("auto");
                Assert.Eq(Tr.Detect(System.Globalization.CultureInfo.CurrentUICulture.Name), Tr.Lang, "auto = system");
            }
            finally { Tr.Use("zh"); }
        }

        public static void Test_T_UsesLanguageAndFormats()
        {
            try
            {
                Tr.Use("en");
                Assert.Eq("ScreenSwitch", Tr.T("app.name"), "en");
                Assert.Eq("no.such.key", Tr.T("no.such.key"), "missing key shows the key");
            }
            finally { Tr.Use("zh"); }
        }

        // 文字表本身：每行中英文的占位一致
        public static void Test_Table_PlaceholdersMatch()
        {
            foreach (KeyValuePair<string, string[]> kv in Tr.All())
            {
                string zh = string.Join(",", Placeholders(kv.Value[0]).ToArray());
                string en = string.Join(",", Placeholders(kv.Value[1]).ToArray());
                Assert.Eq(zh, en, "placeholders of " + kv.Key);
            }
        }

        static List<string> Placeholders(string s)
        {
            var l = new List<string>();
            foreach (Match m in Regex.Matches(s, @"\{\d+\}")) if (!l.Contains(m.Value)) l.Add(m.Value);
            l.Sort();
            return l;
        }

        // 源代码里用到的每个编号都在文字表里
        public static void Test_AllKeysUsedInSourceExist()
        {
            string src = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\src"));
            Assert.True(Directory.Exists(src), "source folder found: " + src);
            foreach (string f in Directory.GetFiles(src, "*.cs"))
                foreach (Match m in Regex.Matches(File.ReadAllText(f), "Tr\\.T\\(\"([^\"]+)\""))
                    Assert.True(Tr.Has(m.Groups[1].Value), Path.GetFileName(f) + " uses missing key " + m.Groups[1].Value);
        }
    }
}
