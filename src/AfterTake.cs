using System;
using System.Diagnostics;
using System.Threading;

namespace ScreenSwitch
{
    // 拿回主屏之后执行配置里的 afterTake.command（空 = 不执行）。显示拓扑变化后有的常驻程序需要重启才恢复
    // （实机：Deskflow 服务端在布局切换后不再响应鼠标越界与热键，只有重启 deskflow-core 才好）。
    // 延迟 delayMs 再执行，等拓扑稳定；延迟期内的多次拿回合并成一次。命令在后台线程执行，不占轮询。
    public class AfterTakeRunner
    {
        const int RUN_TIMEOUT_MS = 15000;
        readonly string command; readonly int delayMs; readonly Log log; readonly Func<string, int> run;
        readonly object gate = new object();
        Timer timer;

        public AfterTakeRunner(string command, int delayMs, Log log) : this(command, delayMs, log, RunShell) { }

        public AfterTakeRunner(string command, int delayMs, Log log, Func<string, int> run)
        {
            this.command = command == null ? "" : command.Trim();
            this.delayMs = Math.Max(0, delayMs); this.log = log; this.run = run;
        }

        public void Schedule()
        {
            if (command.Length == 0) return;
            lock (gate)
            {
                if (timer == null) timer = new Timer(Fire, null, delayMs, Timeout.Infinite);
                else timer.Change(delayMs, Timeout.Infinite);
            }
        }

        void Fire(object state)
        {
            try { log.Info("afterTake: " + command + " -> " + run(command)); }
            catch (Exception e) { log.Error("afterTake: " + e.Message); }
        }

        // 返回退出码；超时返回 -1
        static int RunShell(string command)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + command);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(RUN_TIMEOUT_MS)) { try { p.Kill(); } catch (Exception) { } return -1; }
                return p.ExitCode;
            }
        }
    }
}
