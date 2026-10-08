using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ScreenSwitch
{
    // 开机自动运行（docs/settings-ui-design.md 第 6 节）："启动"文件夹里的 ScreenSwitch.lnk，与 install.cmd 建的是同一个文件。
    // 不写进配置文件。快捷方式用 WScript.Shell 建（经反射调用，不需要额外引用）。
    public class Autostart
    {
        readonly string exe, workDir;
        public string LinkPath { get; private set; }

        public Autostart(string startupFolder, string exePath, string workDir)
        {
            LinkPath = Path.Combine(startupFolder, "ScreenSwitch.lnk");
            exe = exePath; this.workDir = workDir;
        }

        public static Autostart ForCurrentUser(string dir)
        {
            return new Autostart(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Path.Combine(dir, "ScreenSwitch.exe"), dir);
        }

        public bool Enabled { get { return File.Exists(LinkPath); } }

        public void Enable()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LinkPath));
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { LinkPath });
                try
                {
                    Type st = sc.GetType();
                    st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
                    st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
                    st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                }
                finally { Marshal.ReleaseComObject(sc); }
            }
            finally { Marshal.ReleaseComObject(shell); }
        }

        public void Disable()
        {
            if (File.Exists(LinkPath)) File.Delete(LinkPath);
        }
    }
}
