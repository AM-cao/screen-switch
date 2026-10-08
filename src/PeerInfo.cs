using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ScreenSwitch
{
    // request info 的回答（设计文档 5.4）：一行 ASCII，空格分隔的 键=值。
    //   proto=3 os=mac code=15 name=TWFjQm9vaw== devices=17:U3dpdGNo,18:UFM1
    // 名字只用 Base64（UTF-8）传，避免两端终端编码不同；解析方忽略不认识的键。
    public class PeerInfo
    {
        public int Proto;
        public string Os = "";
        // 对方自己的编码，未知为 0
        public uint Code;
        public string Name = "";
        public List<Device> Devices = new List<Device>();

        public static string Format(Config cfg)
        {
            var sb = new StringBuilder();
            sb.Append("proto=3 os=windows code=").Append(cfg.SelfCode.ToString(CultureInfo.InvariantCulture));
            sb.Append(" name=").Append(B64(cfg.SelfName));
            sb.Append(" devices=");
            for (int i = 0; i < cfg.Devices.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(cfg.Devices[i].Code.ToString(CultureInfo.InvariantCulture)).Append(':').Append(B64(cfg.Devices[i].Name));
            }
            return sb.ToString();
        }

        // text 是对方的标准输出：取第一行以 proto= 开头的。没有这样的行（连不上、对方是旧版本）返回 null。
        public static PeerInfo Parse(string text)
        {
            if (text == null) return null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("proto=")) return ParseLine(line);
            }
            return null;
        }

        static PeerInfo ParseLine(string line)
        {
            var info = new PeerInfo();
            foreach (string token in line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = token.IndexOf('=');
                if (eq <= 0) continue;
                string key = token.Substring(0, eq), val = token.Substring(eq + 1);
                if (key == "proto") int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out info.Proto);
                else if (key == "os") info.Os = val;
                else if (key == "code") info.Code = CodeOrZero(val);
                else if (key == "name") info.Name = UnB64(val);
                else if (key == "devices")
                {
                    foreach (string item in val.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int colon = item.IndexOf(':');
                        if (colon <= 0) continue;
                        uint code = CodeOrZero(item.Substring(0, colon));
                        if (code == 0) continue;
                        info.Devices.Add(new Device(UnB64(item.Substring(colon + 1)), code));
                    }
                }
            }
            return info;
        }

        static uint CodeOrZero(string s)
        {
            uint code;
            return Cli.ParseCode(s, out code) ? code : 0;
        }

        static string B64(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }

        static string UnB64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch (FormatException) { return ""; }
        }
    }
}
