using System;
using System.IO;
using System.Threading;

namespace ScreenSwitch
{
    public interface INotifier { void Notify(string title, string text); }
    public interface IClock { void Sleep(int ms); long Now(); }

    public class RealClock : IClock
    {
        public void Sleep(int ms) { Thread.Sleep(ms); }
        public long Now() { return StateFile.Now(); }
    }

    public class Orchestrator
    {
        readonly IDdc ddc; readonly ILayout layout; readonly IRelay relay; readonly INotifier notifier; readonly IClock clock;
        readonly Config cfg; readonly Log log; readonly string dir;
        readonly OwnershipTracker tracker;
        readonly object gate = new object();
        readonly int pollDivisor;
        readonly Prefs prefs;
        readonly string prefsPath;
        readonly DirectRecord directRecord;
        int tick;
        // 上一次读数与归属不符：下一拍立即复核，而不是等下一个轮询周期（与 Mac 一致）。
        bool recheck;
        // 主动拿回后，显示器切过来之前读取仍会失败；这段时间内不因读失败而让出。
        long graceUntil;
        const int GRACE_SECONDS = 8;
        // 本程序自己拿回（改变主屏的显示模式）后显示器会重新同步，期间读取可能失败：短暂宽限，
        // 免得下一拍复核把刚拿回的主屏又让出去，形成自己引起的拿回/让出循环。
        const int TAKE_SETTLE_SECONDS = 4;
        // 从仅主屏打开副屏后，等显示器重新同步、DDC 能读到再写输入（每 0.5 秒读一次，最多 3 秒）。
        const int SETTLE_BEFORE_WRITE_MS = 3000;
        // 让出前把副屏打开（Release，必要时回退扩展）之后，拓扑读数可能短暂是 Missing（显示器重新同步）：
        // 读到 Missing 就每 250 毫秒重查，最多 3 秒，与 CcdLayout.TakeMainOnly 的宽限一致。
        const int GIVE_MISSING_SETTLE_MS = 3000, GIVE_MISSING_POLL_MS = 250;
        // 自动记住排列：每 20 拍（约 10 秒）比对一次；与快照不同的签名要连续两次比对一致才保存。
        const int AUTO_SAVE_TICKS = 20;
        string pendingSig;
        // 布局失败后自愈（参照 Mac 的 LAYOUT_FAIL_AT / LAYOUT_RETRY_SECS）：DoTake / DoRelease 失败时记下时间，
        // 10 秒后由 Tick 按当前归属重试（占有 DoTake，未占有 DoRelease），成功即清除。
        // 实机：UAC 提示框（安全桌面）期间 SetDisplayConfig 一律返回 ERROR_ACCESS_DENIED，持续约 70 秒。
        // 重试失败不再通知（开头那次已通知）；缺屏时推迟重试（见 SelfHeal），retryDeferred 保证推迟只记一次日志。
        const int LAYOUT_RETRY_SECONDS = 10;
        bool layoutFailed;
        long layoutFailAt;
        bool retryDeferred;

        static string HINT_BUTTON { get { return Tr.T("orch.hint-button"); } }

        public event Action OwnedChanged;
        // 布局拿回成功后回调（托盘程序用来执行 afterTake 命令）；失败时不调用
        public Action AfterTake;
        // 命令文件 reload（命令行改完配置后写入，只在本机用）：托盘程序重新读取配置
        public event Action ReloadRequested;

        public Orchestrator(IDdc ddc, ILayout layout, IRelay relay, INotifier notifier, IClock clock, Config cfg, Log log, string dir)
        {
            this.ddc = ddc; this.layout = layout; this.relay = relay; this.notifier = notifier; this.clock = clock;
            this.cfg = cfg; this.log = log; this.dir = dir;
            // 偏好要在启动对账之前读入：对账按"拿回后的布局"判断拓扑是否一致
            prefsPath = Path.Combine(dir, "prefs.json");
            prefs = Prefs.Load(prefsPath);
            directRecord = new DirectRecord(dir);
            pollDivisor = Math.Max(1, cfg.PollMs / 500);
            bool ok = Read();
            tracker = new OwnershipTracker(cfg.Debounce, ok);
            WriteState();
            log.Info("orchestrator start, owned=" + ok);
            if (cfg.SelfCode == 0) log.Info("self.code is 0 (unknown): any successful DDC read counts as owning the main screen; run \"ScreenSwitchCli.exe setup\" to set it");
            log.Info("prefs: ownedLayout=" + Prefs.Name(prefs.OwnedLayout) + " autoSaveLayout=" + (prefs.AutoSaveLayout ? "true" : "false"));
            Reconcile(ok);
        }

        // 启动时可能错过了一次切换：让实际拓扑与所有权对齐。
        // 未占有：只要不是克隆就让出（也覆盖仅主屏——克隆会把副屏打开）；
        // 占有：拓扑与拿回后的布局不符就拿回，缺屏时不动。
        void Reconcile(bool owned)
        {
            if (!layout.HasSnapshot) return;
            LayoutMode mode = layout.Mode;
            string action = "none";
            if (!owned && mode != LayoutMode.Clone) { action = "release"; DoRelease(); }
            else if (owned && mode != Want() && mode != LayoutMode.Missing) { action = "take"; DoTake(); }
            log.Info("startup reconcile: " + action);
        }

        public bool Owned { get { return tracker.Owned; } }

        // 偏好的读取不加锁（与 Owned 一样）：菜单在界面线程上读，切换流程持锁期间不能卡住界面。
        public OwnedLayout OwnedLayout { get { return prefs.OwnedLayout; } }
        public bool AutoSaveLayout { get { return prefs.AutoSaveLayout; } }

        // 占有时应处于的拓扑
        LayoutMode Want()
        {
            return MainOnlyWanted ? LayoutMode.MainOnly : LayoutMode.Extend;
        }

        // 拿回后要不要关掉副屏。只有主屏（layout: none）时这个偏好不起作用。
        bool MainOnlyWanted { get { return !cfg.LayoutNone && prefs.OwnedLayout == OwnedLayout.MainOnly; } }

        void WriteState()
        {
            try { StateFile.WriteState(dir, tracker.Owned, clock.Now()); } catch (Exception e) { log.Error("state write: " + e.Message); }
        }

        void SavePrefs()
        {
            try { prefs.Save(prefsPath); } catch (Exception e) { log.Error("prefs save: " + e.Message); }
        }

        void Fire()
        {
            Action h = OwnedChanged;
            if (h != null) h();
        }

        // 归属（设计文档 6.1）：读 VCP 0x60 成功，且值的低 8 位等于本机编码，才算主屏在本机
        // （有的显示器在非活跃口上也应答，读取成功不等于主屏在本机）。本机编码未知（0）时读取成功即占有。
        bool Read()
        {
            uint v;
            if (!ddc.TryReadInput(out v)) return false;
            return cfg.SelfCode == 0 || (v & 0xFF) == cfg.SelfCode;
        }

        // 按"拿回后的布局"拿回。仅主屏失败时退回双屏扩展，保证至少有可用的桌面。
        // 返回实际生效的布局：MainOnly / Extend；两者都失败时返回 Missing。
        // 只在占有（或刚置为占有）时调用：拿回之后一律进入短暂的稳定宽限期（与主动拿回的 8 秒宽限取较晚者）。
        // 失败（仅主屏与退回扩展都失败）时记下失败，10 秒后由 Tick 重试；仅主屏失败但退回扩展成功算成功（桌面可用，
        // 不重试，否则每 10 秒闪一次屏并重复"无法关闭副屏"的通知；与 Mac 的 do_take 一致）。
        LayoutMode DoTake() { return DoTake(false); }

        // retry：由 SelfHeal 重试发起，失败时不再通知（见 NotifyLayoutFailure）
        LayoutMode DoTake(bool retry)
        {
            try
            {
                LayoutMode applied;
                if (MainOnlyWanted)
                {
                    try { layout.Take(OwnedLayout.MainOnly); applied = LayoutMode.MainOnly; }
                    catch (Exception e)
                    {
                        log.Error("take main-only: " + e.Message + ", falling back to extend");
                        layout.Take(OwnedLayout.Extend);
                        notifier.Notify(Tr.T("orch.main-only-failed"), e.Message);
                        applied = LayoutMode.Extend;
                    }
                }
                else { layout.Take(OwnedLayout.Extend); applied = LayoutMode.Extend; }
                LayoutSucceeded("take");
                if (AfterTake != null) AfterTake();
                return applied;
            }
            catch (Exception e)
            {
                log.Error("take: " + e.Message);
                NotifyLayoutFailure(Tr.T("orch.take-layout-failed"), e.Message, retry);
                MarkLayoutFailed("take");
                return LayoutMode.Missing;
            }
            finally { graceUntil = Math.Max(graceUntil, clock.Now() + TAKE_SETTLE_SECONDS); }
        }

        // 让出失败（含开副屏兜底之后）记下失败，10 秒后由 Tick 重试让出。兜底打开了副屏（扩展）也照样重试：
        // 未占有时应处于克隆，扩展会让窗口落到正显示别的设备的主屏上。
        void DoRelease() { DoRelease(false); }

        void DoRelease(bool retry)
        {
            try { layout.Release(); LayoutSucceeded("release"); }
            catch (Exception e)
            {
                log.Error("release: " + e.Message);
                NotifyLayoutFailure(Tr.T("orch.release-layout-failed"), e.Message, retry);
                // 让出失败而副屏还关着（仅主屏）：至少把副屏打开，别让本机没有能看到的屏幕
                try
                {
                    if (layout.Mode == LayoutMode.MainOnly)
                    {
                        log.Info("release failed in main-only, enabling secondary");
                        layout.Take(OwnedLayout.Extend);
                    }
                }
                catch (Exception e2) { log.Error("release: enable secondary: " + e2.Message); }
                // 失败时间记在兜底之后：兜底里的 DisplaySwitch 回退可能要几秒，重试间隔从这次尝试结束算起
                MarkLayoutFailed("release");
            }
        }

        // 只有自动重试的失败不再通知（这一段失败开头的那次已经通知过）；轮询切换与用户操作（改布局预设、
        // give windows、拿回主屏、切走）即使发生在失败期间也照常通知，用户的操作总能得到反馈。
        void NotifyLayoutFailure(string title, string text, bool retry)
        {
            if (retry) { log.Info("layout: retry failed, notice suppressed"); return; }
            notifier.Notify(title, text);
        }

        void MarkLayoutFailed(string op)
        {
            layoutFailed = true;
            layoutFailAt = clock.Now();
            retryDeferred = false;
            // 重试以有快照为前提（见 SelfHeal）：没有快照时如实写明要等保存快照之后
            if (layout.HasSnapshot) log.Info("layout: " + op + " failed, retry in " + LAYOUT_RETRY_SECONDS + "s");
            else log.Info("layout: " + op + " failed, no layout snapshot: retry after one is saved");
        }

        void LayoutSucceeded(string op)
        {
            if (!layoutFailed) return;
            layoutFailed = false;
            retryDeferred = false;
            log.Info("layout: recovered (" + op + " ok after an earlier failure)");
        }

        public void Tick()
        {
            if (!Monitor.TryEnter(gate)) return;
            try
            {
                try
                {
                    string cmd = StateFile.ReadAndDeleteCommand(dir);
                    if (cmd != null) { HandleCommandLocked(cmd); return; }
                    tick++;
                    // 自动记住排列按拍计数，与轮询节拍无关；切换流程持锁时这里进不来，不会在流程中比对。
                    if (tick % AUTO_SAVE_TICKS == 0) AutoSaveCheck();
                    // 未占有时每拍都读，尽快跟上主屏切回来的那一刻；占有时按配置的间隔读，读数与归属不符时下一拍复核。
                    bool due = !tracker.Owned || recheck || tick % pollDivisor == 0;
                    if (due && Poll()) return;
                    // 读数与归属不符、下一拍要复核时先不自愈：切换可能就在下一拍，别抢在它前面改拓扑
                    // （例如刚重试拿回，复核的失败读数又落进拿回后的稳定宽限里，让出被推迟）。
                    if (!recheck) SelfHeal();
                }
                catch (Exception e) { log.Error("tick: " + e); }
            }
            finally { Monitor.Exit(gate); }
        }

        // 一次轮询。本拍发生了拿回/让出时返回 true。
        bool Poll()
        {
            bool ok = Read();
            if (!ok && tracker.Owned && clock.Now() < graceUntil) { recheck = false; WriteState(); return false; }
            Transition t = tracker.Feed(ok);
            recheck = t == Transition.None && ok != tracker.Owned;
            if (t == Transition.Take) { log.Info("poll: take"); DoTake(); Fire(); }
            else if (t == Transition.Release) { log.Info("poll: release"); DoRelease(); Fire(); }
            WriteState();
            return t != Transition.None;
        }

        // 布局自愈，只在不忙时（Tick 拿到了 gate、本拍没有切换）进行：
        // - 上次布局操作失败且已过 10 秒：按当前归属重试（占有 DoTake，未占有 DoRelease）。
        // - 没有失败记录：未占有而拓扑是仅主屏 → 让出（克隆会把副屏打开）。未占有时副屏必须亮着，
        //   否则主屏显示别的设备时本机没有能看到的屏幕。与自动记住排列同一节拍，约每 10 秒查一次拓扑。
        // 占有时不做周期性对账：用户手动改的拓扑不去动它，只在本程序自己的布局操作失败后重试。
        // 没有快照时布局操作必然失败，不重试也不检查（与启动对账一致）；保存快照后照常重试。
        // 缺屏（Mode 为 Missing：有一块显示器断电/拔掉或查询失败）时重试也必然失败，而且 Release/扩展要走
        // DisplaySwitch 回退（最多 5 秒 + 3 秒）、仅主屏先等 3 秒 Missing，界面每次卡好几秒、状态文件也可能超过 10 秒
        // 没更新：推迟重试，失败记录保留，仍按 10 秒节拍再看，拓扑恢复后照常重试；推迟只在开始时记一次日志。
        // UAC 那种情况拓扑不是 Missing（仅主屏且副屏可用、或克隆），照常重试。
        void SelfHeal()
        {
            if (layoutFailed)
            {
                if (clock.Now() - layoutFailAt < LAYOUT_RETRY_SECONDS || !layout.HasSnapshot) return;
                if (layout.Mode == LayoutMode.Missing)
                {
                    if (!retryDeferred) { retryDeferred = true; log.Info("layout: retry deferred, a monitor is missing from the topology"); }
                    layoutFailAt = clock.Now();
                    return;
                }
                if (retryDeferred) { retryDeferred = false; log.Info("layout: topology back, retrying"); }
                // 重试可能阻塞数秒（DisplaySwitch 回退）：先刷新状态文件（设计文档 5.2，与 TakeBack 中继前一致）
                WriteState();
                if (tracker.Owned) { log.Info("layout: retrying take"); DoTake(true); }
                else { log.Info("layout: retrying release"); DoRelease(true); }
                WriteState();
                return;
            }
            if (tracker.Owned || tick % AUTO_SAVE_TICKS != 0 || !layout.HasSnapshot) return;
            if (layout.Mode != LayoutMode.MainOnly) return;
            log.Info("layout: not owned but main-only, enabling secondary");
            DoRelease();
            WriteState();
        }

        // 参照 Mac 的 auto_save_check：占有、双屏扩展、排列与快照不同且连续两次比对一致时保存快照。
        // 布局失败尚未恢复时不比对（Mac 同样要求 LAYOUT_FAIL_AT 为 0）：此时的排列不是本程序应用的，不能记成快照。
        // 只在状态变化时写日志（记下待定、已保存、保存失败），每次比对本身不写。
        void AutoSaveCheck()
        {
            try
            {
                if (!prefs.AutoSaveLayout || !tracker.Owned || layoutFailed || !layout.HasSnapshot || layout.Mode != LayoutMode.Extend)
                {
                    pendingSig = null; return;
                }
                string cur = layout.CurrentSignature();
                string saved = layout.SnapshotSignature();
                if (cur == null || saved == null || cur == saved) { pendingSig = null; return; }
                if (cur != pendingSig)
                {
                    pendingSig = cur;
                    log.Info("layout: arrangement changed, save if unchanged at next check");
                    return;
                }
                pendingSig = null;
                try
                {
                    layout.SaveSnapshot();
                    log.Info("layout: arrangement auto-saved");
                    notifier.Notify(Tr.T("orch.auto-saved"), "");
                }
                catch (Exception e) { log.Error("layout: auto-save failed: " + e.Message); }
            }
            catch (Exception e) { pendingSig = null; log.Error("layout: auto-save check: " + e.Message); }
        }

        public void HandleCommand(string line)
        {
            lock (gate) { HandleCommandLocked(line); }
        }

        void HandleCommandLocked(string line)
        {
            log.Info("command: " + line);
            // 命令文件（设计文档 5.1）：give peer、give code <n>、give self、pre-take；
            // 旧内容 give mac、give windows、give other 照常认。
            uint code;
            if (line == "give self" || line == "give windows") { if (tracker.Owned) DoTake(); else log.Info(line + " ignored: not owned"); }
            else if (line == "give peer" || line == "give mac") GiveToPeerLocked();
            else if (line.StartsWith("give code ") && Cli.ParseCode(line.Substring("give code ".Length), out code)) GiveToDeviceLocked(code);
            else if (line == "give other")
            {
                // 旧内容：切到设备列表里的第一个设备
                if (cfg.Devices.Count > 0) GiveToDeviceLocked(cfg.Devices[0].Code);
                else log.Info("give other ignored: no devices configured");
            }
            else if (line == "reload")
            {
                Action h = ReloadRequested;
                if (h != null) h();
            }
            else if (line == "pre-take")
            {
                // 对方的 request take：它马上要把主屏切过来。已占有时主屏本来就在本机，不动。
                if (tracker.Owned) log.Info("pre-take ignored: already owned");
                else PreTakeLocked("peer request take");
            }
            else log.Error("unknown command: " + line);
        }

        public void GiveToPeer()
        {
            lock (gate) { GiveToPeerLocked(); }
        }

        public void GiveToDevice(uint code)
        {
            lock (gate) { GiveToDeviceLocked(code); }
        }

        public void TakeBack()
        {
            lock (gate)
            {
                if (tracker.Owned) { DoTake(); notifier.Notify(Tr.T("orch.already-here"), ""); return; }
                // 先直接切，再请对方代发（设计文档 6.3）：向主屏写本机编码，不等结果
                bool direct = DirectWrite(cfg.SelfCode);
                string why = NO_PEER;
                if (cfg.PeerConfigured)
                {
                    // 中继（解析对方地址最多 3 秒 + ssh）会占住 gate 约 8.5 秒，期间轮询停着不写状态文件；
                    // 先刷新一次，免得对端读到超过 10 秒的时间戳而判定本机守护进程未运行（设计文档 5.2）。
                    WriteState();
                    int rc = relay.RequestGivePeer();
                    if (rc == 0)
                    {
                        LayoutMode applied = PreTakeLocked("takeback, peer accepted give peer");
                        // 按实际生效的布局写通知：仅主屏失败退回扩展时不能说"仅主屏"；只有主屏时没有布局可说
                        string body = cfg.LayoutNone ? ""
                            : applied == LayoutMode.MainOnly ? Tr.T("orch.layout-main-only")
                            : applied == LayoutMode.Extend ? Tr.T("orch.layout-extend") : Tr.T("orch.layout-not-restored");
                        notifier.Notify(Tr.T("orch.asked-give-back", cfg.PeerName), body);
                        return;
                    }
                    why = PeerFailed(rc);
                }
                // 对方没有代发：直接写入生效的话，3 秒内能读到本机编码，按正常拿回处理，不再提示。
                // 直接切换在这块显示器上从没成功过、且已经连续几次等不到结果时不再等（见 DirectRecord）。
                if (direct && directRecord.ShouldWait)
                {
                    if (WaitOwned(DIRECT_CONFIRM_MS))
                    {
                        log.Info("takeback: the direct write switched the main screen");
                        directRecord.Succeeded();
                        tracker.Reset(true); DoTake(); WriteState(); Fire();
                        return;
                    }
                    if (directRecord.Missed()) log.Info("takeback: direct writes never took effect, not waiting for them from now on (running setup resets this)");
                }
                notifier.Notify(HINT_BUTTON, why);
            }
        }

        static string NO_PEER { get { return Tr.T("orch.no-peer"); } }
        // 直接写入之后最多等这么久确认（每 0.5 秒读一次）
        const int DIRECT_CONFIRM_MS = 3000;

        string PeerFailed(int rc)
        {
            return Tr.T("orch.peer-failed", cfg.PeerName, rc);
        }

        // 未占有时直接向主屏写输入口编码（directTake 关着、编码未知时不写）。有的显示器在非活跃口上也接受
        // DDC 指令；只认活跃口的显示器会忽略这次写入。返回写入调用本身是否成功——失败说明显示器没有收到，
        // 调用方不必再等它生效。
        bool DirectWrite(uint code)
        {
            if (!cfg.DirectTake || code == 0) return false;
            bool ok = ddc.WriteInput(code);
            log.Info("direct write 0x" + code.ToString("X") + " -> " + ok);
            return ok;
        }

        // 每 0.5 秒读一次，连续 debounce 次占有即返回 true
        bool WaitOwned(int ms)
        {
            int need = Math.Max(1, cfg.Debounce), waited = 0, streak = 0;
            while (waited < ms)
            {
                clock.Sleep(500); waited += 500;
                WriteState();
                if (!Read()) streak = 0;
                else if (++streak >= need) return true;
            }
            return false;
        }

        // 主屏马上要切过来（本机拿回被对方接受，或对方发来 request take，设计文档 6.3）：先恢复拿回后的布局，
        // 显示器切过来时直接显示，不经过镜像阶段；随后 8 秒宽限期内读失败不让出，期满仍读不到才让出（Poll）。
        // 调用方保证未占有。返回实际生效的布局（见 DoTake）。
        LayoutMode PreTakeLocked(string reason)
        {
            tracker.Reset(true);
            // owned=1 立刻发布，不等 DoTake()（仅主屏可能要等 Missing 消退，较慢）：中继已经占用了
            // 一段时间，DoTake() 再慢下去，对端读到的时间戳就可能超过 10 秒（设计文档 5.2）。
            WriteState();
            graceUntil = clock.Now() + GRACE_SECONDS;
            LayoutMode applied = DoTake(); WriteState(); Fire();
            log.Info("pre-take: " + reason + ", grace " + GRACE_SECONDS + "s");
            return applied;
        }

        public void SaveLayout()
        {
            lock (gate) { pendingSig = null; layout.SaveSnapshot(); }
        }

        // 修改拿回后的布局：占有时立即生效，未占有时下次拿回生效。
        public void SetOwnedLayout(OwnedLayout v)
        {
            lock (gate)
            {
                prefs.OwnedLayout = v;
                SavePrefs();
                pendingSig = null;
                log.Info("prefs: ownedLayout=" + Prefs.Name(v));
                if (tracker.Owned) DoTake();
                else notifier.Notify(Tr.T("orch.layout-pref-changed"), Tr.T("orch.layout-pref-changed.text"));
            }
        }

        public void SetAutoSaveLayout(bool on)
        {
            lock (gate)
            {
                prefs.AutoSaveLayout = on;
                SavePrefs();
                pendingSig = null;
                log.Info("prefs: autoSaveLayout=" + (on ? "true" : "false"));
            }
        }

        // 退出前：副屏若被关着（仅主屏），把它打开，程序退出后不留下一块黑着的副屏。
        public void PrepareExit()
        {
            lock (gate)
            {
                try
                {
                    if (layout.Mode == LayoutMode.MainOnly)
                    {
                        log.Info("exit: main-only, enabling secondary");
                        layout.Take(OwnedLayout.Extend);
                    }
                }
                catch (Exception e) { log.Error("exit: enable secondary: " + e.Message); }
            }
        }

        // 主动切走前：仅主屏时先让副屏亮起来（与 Mac 对称）。直接进入克隆（少闪一次），
        // 不行再退回扩展；两者都不行就取消切换，不写 DDC。
        // 拓扑是自己刚改的，显示器随之重新同步，期间 Mode 读数可能短暂是 Missing（实测 1-3 秒），DDC 也
        // 可能不响应：Mode 用 WaitWhileMissing 等它消退再判断，DDC 等到能读到再写。
        // release() 本身没有报错（CcdLayout 只在 SetDisplayConfig 确认成功或 /clone 回退校验通过后才返回），只是
        // 读数还没跟上（Missing），就相信调用已生效、继续切换，不能当作失败取消——否则会把拓扑晾在克隆里却不再往下走；
        // 这时只有仍读到仅主屏才回退扩展。
        // release() 报错时不能相信任何没确认的东西：只要等待后的拓扑不是克隆/扩展（包括一直 Missing）就回退扩展，
        // 最终拓扑仍不是克隆/扩展就取消切换。
        // 入口处 Mode 只读一次：克隆标记与"要不要先开副屏"出自同一次读数。
        // out cloned：结束时拓扑是否（已经或应当）处于克隆，供调用方在写 DDC 之后判断要不要再让出一次
        // ——那时显示器正在切输入，Mode 可能又短暂读到 Missing，不能重读，只能用这里记下的结论。
        bool EnsureSecondaryBeforeGive(out bool cloned)
        {
            LayoutMode entry = layout.Mode;
            cloned = entry == LayoutMode.Clone;
            if (entry != LayoutMode.MainOnly) return true;
            log.Info("give: main-only, enabling secondary first");
            bool releaseFailed = false;
            try { layout.Release(); }
            catch (Exception e) { releaseFailed = true; log.Error("give: clone from main-only: " + e.Message); }
            cloned = !releaseFailed;
            LayoutMode m = WaitGiveMissing();
            bool lit = m == LayoutMode.Clone || m == LayoutMode.Extend;
            if (releaseFailed ? !lit : m == LayoutMode.MainOnly)
            {
                try { layout.Take(OwnedLayout.Extend); }
                catch (Exception e) { log.Error("give: extend from main-only: " + e.Message); }
                cloned = false;
                m = WaitGiveMissing();
                lit = m == LayoutMode.Clone || m == LayoutMode.Extend;
            }
            if (lit) { WaitReadable(SETTLE_BEFORE_WRITE_MS); return true; }
            if (!releaseFailed)
            {
                log.Info("give: mode still " + m + " after settling, proceeding (release reported success)");
                WaitReadable(SETTLE_BEFORE_WRITE_MS);
                return true;
            }
            cloned = false;
            log.Error("give: secondary not restored (mode " + m + "), cancelled");
            notifier.Notify(Tr.T("orch.second-not-restored"), Tr.T("orch.second-not-restored.text"));
            // 走到这里时回退扩展一定已经调用过（release() 报错且拓扑没亮）。最终读数既不是入口时的仅主屏、也不是
            // 拿回后的布局（例如一直 Missing，扩展其实可能已生效）：拓扑状态不明，记为布局失败，由占有时的拿回重试
            // 恢复拿回后的布局（缺屏时推迟到拓扑恢复）。读数仍是仅主屏说明拓扑没被改动：与预设一致就不必恢复，
            // 与预设不一致则是用户自己切的仅主屏，不去动它。
            if (m != entry && m != Want()) MarkLayoutFailed("give pre-step");
            return false;
        }

        // 读到 Missing 就按 CcdLayout.WaitWhileMissing 的节奏重查（最多 3 秒，每 250 毫秒一次），
        // 读到其他拓扑立即返回。
        LayoutMode WaitGiveMissing()
        {
            int waited;
            LayoutMode m = CcdLayout.WaitWhileMissing(delegate { return layout.Mode; },
                delegate(int ms) { clock.Sleep(ms); WriteState(); }, GIVE_MISSING_SETTLE_MS, GIVE_MISSING_POLL_MS, out waited);
            if (waited > 0) log.Info("give: mode read Missing, " + m + " after " + waited + " ms");
            return m;
        }

        // 每 0.5 秒读一次，读到即返回；到时仍读不到也照常往下写（只记日志）。
        void WaitReadable(int ms)
        {
            int waited = 0;
            while (waited < ms)
            {
                clock.Sleep(500); waited += 500;
                WriteState();
                if (Read()) { log.Info("give: monitor readable after " + waited + " ms"); return; }
            }
            log.Info("give: monitor not readable after " + ms + " ms, writing anyway");
        }

        void GiveToPeerLocked()
        {
            // 主屏不在本机时"切给对方"没有意义（菜单里也没有这一项）；命令文件晚到时会走到这里
            if (!tracker.Owned) { log.Info("give peer ignored: not owned"); return; }
            try
            {
                if (cfg.PeerCode == 0 && !LearnPeerCode())
                {
                    notifier.Notify(Tr.T("orch.peer-code-unknown", cfg.PeerName), Tr.T("orch.run-setup"));
                    return;
                }
                // 最先通知对方：它切布局（约 3 秒）与本机打开副屏、显示器切输入的时间重叠，越早越稳。
                if (cfg.PeerConfigured) NotifyPeerTake();
                if (SwitchAway(cfg.PeerCode) == Give.NoEffect) NotSwitched();
            }
            finally { WriteState(); }
        }

        // peer.code 未知：用 request info 向对方询问，问到了写回配置文件（设计文档 6.3）。
        bool LearnPeerCode()
        {
            if (!cfg.PeerConfigured) return false;
            WriteState();
            PeerInfo info = null;
            try { info = relay.RequestInfo(); }
            catch (Exception e) { log.Error("give: request info: " + e.Message); }
            WriteState();
            if (info == null || info.Code == 0) { log.Info("give: peer code unknown and request info did not provide it"); return false; }
            cfg.PeerCode = info.Code;
            log.Info("give: peer code learned from request info: " + info.Code);
            try { cfg.Save(Path.Combine(dir, "config.json")); }
            catch (Exception e) { log.Error("config save: " + e.Message); }
            return true;
        }

        // 切给某个设备，只切到它的编码（设计文档 6.3）。占有时：写入后最多等 3 秒确认不再占有，让出，
        // 再观察 15 秒；主屏自己跳回来（设备没有信号）就拿回并通知。
        // 未占有时：先直接写该设备的编码，再请对方代发；对方没有代发就读一次，读到该编码说明已经切过去。
        void GiveToDeviceLocked(uint code)
        {
            string name = DeviceName(code);
            if (!tracker.Owned)
            {
                bool direct = DirectWrite(code);
                string why = NO_PEER;
                if (cfg.PeerConfigured)
                {
                    // 中继会占住 gate 约 8.5 秒：先刷新状态文件（见 TakeBack）
                    WriteState();
                    int rc = relay.RequestGiveCode(code);
                    if (rc == 0) { notifier.Notify(Tr.T("orch.asked-give-to", cfg.PeerName, name), ""); return; }
                    why = PeerFailed(rc);
                }
                uint v;
                if (direct && ddc.TryReadInput(out v) && (v & 0xFF) == code)
                {
                    log.Info("give: the direct write switched the main screen to 0x" + code.ToString("X"));
                    return;
                }
                notifier.Notify(HINT_BUTTON, why);
                return;
            }
            try
            {
                Give r = SwitchAway(code);
                if (r == Give.NoEffect) NotSwitched();
                if (r != Give.Switched || !ReturnedWithin(15000)) return;
                log.Info("no signal on 0x" + code.ToString("X") + ", monitor returned");
                tracker.Reset(true); DoTake(); WriteState(); Fire();
                notifier.Notify(Tr.T("orch.no-signal", name), Tr.T("orch.stays-here"));
            }
            finally { WriteState(); }
        }

        string DeviceName(uint code)
        {
            Device d = cfg.FindDevice(code);
            return d != null ? d.Name : "0x" + code.ToString("X");
        }

        // Cancelled：副屏没能亮起来，已取消并通知过；NoEffect：写入失败或主屏没有切走；Switched：已切走并让出
        enum Give { Cancelled, NoEffect, Switched }

        // 占有时把主屏切到 code：必要时先开副屏，写 DDC，最多等 3 秒确认不再占有，然后让出。
        Give SwitchAway(uint code)
        {
            bool cloned;
            if (!EnsureSecondaryBeforeGive(out cloned)) return Give.Cancelled;
            if (!ddc.WriteInput(code)) { log.Error("write failed 0x" + code.ToString("X")); return Give.NoEffect; }
            if (!WaitNotOwned(3000)) { log.Info("no effect 0x" + code.ToString("X")); return Give.NoEffect; }
            tracker.Reset(false);
            // 写 DDC 前已从仅主屏进入克隆时不再让出一次（与 Mac 的 apply_layout 一致：拓扑已符合就不动）。
            // 用 EnsureSecondaryBeforeGive 返回时记下的标记判断，这里不重读 layout.Mode：显示器正在
            // 切换输入，此刻的 Mode 读数可能短暂是 Missing，重读会误判成没克隆而多让出一次。
            // 已在克隆时"未占有"的布局已经就位：之前拿回失败的记录随之作废，否则 10 秒后会对克隆再让出一次。
            if (cloned) { log.Info("give: already cloned, release skipped"); LayoutSucceeded("clone"); }
            else DoRelease();
            WriteState(); Fire();
            return Give.Switched;
        }

        // 没有切走：为切换打开的副屏按拿回后的布局恢复（缺屏时不动，与启动对账一致），并通知
        void NotSwitched()
        {
            LayoutMode m = layout.Mode;
            if (m != Want() && m != LayoutMode.Missing) { log.Info("give: not switched, restoring owned layout"); DoTake(); }
            notifier.Notify(Tr.T("orch.not-switched"), Tr.T("orch.monitor-no-response"));
        }

        // 切给对方之前发 request take（设计文档 5.2），让对方预先恢复布局。尽力而为：结果只记日志，
        // 不支持（1）、未运行（3）、连不上（255）、中继异常都照常往下写 DDC，也不弹通知。
        // 中继最多占住 gate 约 5 秒：先刷新状态文件（见 TakeBack）。
        void NotifyPeerTake()
        {
            WriteState();
            try
            {
                int rc = relay.RequestTake();
                if (rc == 0) log.Info("give: peer accepted request take");
                else log.Info("give: peer did not accept request take (" + rc + "), switching anyway");
            }
            catch (Exception e) { log.Error("give: request take: " + e.Message + ", switching anyway"); }
            WriteState();
        }

        // 写入后等主屏不再在本机（每 0.5 秒读一次）：读取失败，或读到的不是本机编码
        bool WaitNotOwned(int ms)
        {
            int waited = 0;
            while (waited < ms)
            {
                clock.Sleep(500); waited += 500;
                WriteState();
                if (!Read()) return true;
            }
            return false;
        }

        bool ReturnedWithin(int ms)
        {
            int waited = 0, okStreak = 0;
            while (waited < ms)
            {
                clock.Sleep(1000); waited += 1000;
                WriteState();
                if (Read()) { okStreak++; if (okStreak >= 2) return true; }
                else okStreak = 0;
            }
            return false;
        }
    }
}
