using System;
using System.IO;

namespace ScreenSwitch
{
    public class Log
    {
        readonly string path;
        readonly object gate = new object();
        public Log(string path) { this.path = path; }
        public void Info(string msg) { Write("INFO", msg); }
        public void Error(string msg) { Write("ERROR", msg); }
        void Write(string level, string msg)
        {
            lock (gate)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + msg + Environment.NewLine);
                }
                catch (Exception) { }
            }
        }
    }
}
