using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ScreenSwitch
{
    public static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                AttachConsole(-1);
                var stdout = new StreamWriter(Console.OpenStandardOutput());
                stdout.AutoFlush = true;
                Console.SetOut(stdout);
                return Cli.Run(args, Paths.Dir);
            }
            bool created;
            using (var mutex = new System.Threading.Mutex(true, "Local\\ScreenSwitch.Tray", out created))
            {
                if (!created) return 0;
                var log = new Log(Paths.Log);
                System.Windows.Forms.Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    log.Error("ui thread: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    log.Error("unhandled: " + e.ExceptionObject);
                };
                System.Windows.Forms.Application.EnableVisualStyles();
                try { System.Windows.Forms.Application.Run(new TrayApp(Paths.Dir)); }
                catch (Exception e) { log.Error("fatal: " + e); return 1; }
            }
            return 0;
        }
    }
}
