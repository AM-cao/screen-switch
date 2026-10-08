using System;
using System.IO;

namespace ScreenSwitch
{
    // 直接切换（设计文档 6.3）有没有成功过的记录，存在安装目录下的 direct-take.txt。
    // 只认活跃口的显示器会忽略非活跃口上的写入，每次都等 3 秒只会让失败提示晚 3 秒：
    // 从没成功过、且已经连续 3 次等不到结果时，之后不再等（仍然写入）；成功过一次就一直等。
    // 重新运行设置时这个文件被删掉，记录从头开始。
    public class DirectRecord
    {
        public const int MAX_MISSES = 3;
        readonly string path;
        bool worked;
        int misses;

        public DirectRecord(string dir)
        {
            path = Path.Combine(dir, "direct-take.txt");
            try
            {
                if (!File.Exists(path)) return;
                string text = File.ReadAllText(path).Trim();
                if (text == "ok") worked = true;
                else if (text.StartsWith("miss ")) int.TryParse(text.Substring(5), out misses);
            }
            catch (IOException) { }
        }

        public bool ShouldWait { get { return worked || misses < MAX_MISSES; } }

        public void Succeeded()
        {
            if (worked) return;
            worked = true; misses = 0;
            Save();
        }

        // 返回 true：这次之后不再等了
        public bool Missed()
        {
            if (worked) return false;
            misses++;
            Save();
            return misses == MAX_MISSES;
        }

        void Save()
        {
            try { File.WriteAllText(path, worked ? "ok" : "miss " + misses); }
            catch (Exception) { }
        }
    }
}
