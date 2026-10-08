using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenSwitch
{
    // 设置问答的读写。真正的控制台用 ReadConsoleW / WriteConsoleW（UTF-16，与代码页无关：代码页 936 的窗口里
    // 中文照常显示，输入的中文名字也读得对）。被重定向时（管道、文件、ssh）输出沿用 Console.Out
    //（Program.Main 设的 UTF-8），输入按 UTF-8 读，不是合法的 UTF-8 时按系统代码页读。
    public static class ConsoleIo
    {
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
        [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WriteConsoleW(IntPtr h, char[] buf, uint n, out uint written, IntPtr reserved);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ReadConsoleW(IntPtr h, [Out] char[] buf, uint n, out uint read, IntPtr control);

        const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11;

        // 只有真正的控制台句柄 GetConsoleMode 才成功
        static bool IsConsole(IntPtr h)
        {
            uint mode;
            return h != IntPtr.Zero && h != new IntPtr(-1) && GetConsoleMode(h, out mode);
        }

        public static bool InputIsConsole { get { return IsConsole(GetStdHandle(STD_INPUT_HANDLE)); } }

        public static TextWriter Out()
        {
            IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
            return IsConsole(h) ? (TextWriter)new ConsoleWriter(h) : Console.Out;
        }

        public static TextReader In()
        {
            IntPtr h = GetStdHandle(STD_INPUT_HANDLE);
            if (IsConsole(h)) return new ConsoleReader(h);
            return RedirectedReader(Console.OpenStandardInput(), Encoding.Default);
        }

        // 按行读重定向的输入（只实现 ReadLine）：每行先按 UTF-8 解码，不合法时按 fallback 解码
        public static TextReader RedirectedReader(Stream stream, Encoding fallback)
        {
            return new LineReader(stream, fallback);
        }

        static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        // 纯函数：一行的字节 → 文字
        public static string DecodeLine(byte[] bytes, Encoding fallback)
        {
            try { return StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { return fallback.GetString(bytes); }
        }

        class ConsoleWriter : TextWriter
        {
            readonly IntPtr handle;
            public ConsoleWriter(IntPtr handle) { this.handle = handle; }
            public override Encoding Encoding { get { return Encoding.Unicode; } }
            public override void Write(char value) { Write(new char[] { value }, 0, 1); }
            public override void Write(string value) { if (value != null) Write(value.ToCharArray(), 0, value.Length); }
            public override void Write(char[] buffer, int index, int count)
            {
                while (count > 0)
                {
                    int n = Math.Min(count, 4096);
                    var chunk = new char[n];
                    Array.Copy(buffer, index, chunk, 0, n);
                    uint written;
                    if (!WriteConsoleW(handle, chunk, (uint)n, out written, IntPtr.Zero)) return;
                    index += n; count -= n;
                }
            }
        }

        class ConsoleReader : TextReader
        {
            readonly IntPtr handle;
            public ConsoleReader(IntPtr handle) { this.handle = handle; }
            // 行输入模式下一次返回一整行（带 CRLF）；行比缓冲区长时分几次返回。Ctrl+Z 回车 = 输入结束。
            public override string ReadLine()
            {
                var sb = new StringBuilder();
                var buf = new char[1024];
                for (;;)
                {
                    uint read;
                    if (!ReadConsoleW(handle, buf, (uint)buf.Length, out read, IntPtr.Zero) || read == 0)
                    {
                        if (sb.Length == 0) return null;
                        break;
                    }
                    sb.Append(buf, 0, (int)read);
                    if (buf[read - 1] == '\n') break;
                }
                string s = sb.ToString();
                if (s.Length > 0 && s[0] == (char)0x1A) return null;
                return s.TrimEnd('\r', '\n');
            }
        }

        class LineReader : TextReader
        {
            readonly Stream stream;
            readonly Encoding fallback;
            bool first = true;
            public LineReader(Stream stream, Encoding fallback) { this.stream = stream; this.fallback = fallback; }
            public override string ReadLine()
            {
                var ms = new MemoryStream();
                bool any = false;
                for (;;)
                {
                    int b = stream.ReadByte();
                    if (b < 0) break;
                    any = true;
                    if (b == '\n') break;
                    ms.WriteByte((byte)b);
                }
                if (!any) return null;
                byte[] line = ms.ToArray();
                int start = 0, len = line.Length;
                // 文件开头的 UTF-8 BOM、行尾的 CR 不算内容
                if (first && len >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF) { start = 3; len -= 3; }
                first = false;
                if (len > 0 && line[start + len - 1] == '\r') len--;
                var body = new byte[len];
                Array.Copy(line, start, body, 0, len);
                return DecodeLine(body, fallback);
            }
        }
    }
}
