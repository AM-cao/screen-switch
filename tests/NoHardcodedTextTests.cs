using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ScreenSwitch.Tests
{
    // 给人看的文字都要在文字表里（docs/settings-ui-design.md 第 8 节）：源代码里不能有中文字符串字面量
    public static class NoHardcodedTextTests
    {
        public static void Test_NoChineseStringLiteralsInSource()
        {
            string src = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\src"));
            var cjk = new Regex("[　-〿一-鿿＀-￯]");
            var bad = new System.Text.StringBuilder();
            foreach (string f in Directory.GetFiles(src, "*.cs"))
            {
                string[] lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = StripComment(lines[i]);
                    foreach (Match m in Regex.Matches(code, "\"(?:[^\"\\\\]|\\\\.)*\""))
                        if (cjk.IsMatch(m.Value))
                            bad.AppendLine(Path.GetFileName(f) + ":" + (i + 1) + " has a Chinese literal: " + m.Value);
                }
            }
            Assert.True(bad.Length == 0, "\n" + bad);
        }

        // 去掉行尾的 // 注释（不在字符串里的那个 //）
        static string StripComment(string line)
        {
            bool inStr = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inStr && c == '\\') { i++; continue; }
                if (c == '"') inStr = !inStr;
                if (!inStr && c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line.Substring(0, i);
            }
            return line;
        }
    }
}
