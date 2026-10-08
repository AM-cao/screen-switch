namespace ScreenSwitch
{
    // 只有主屏（layout: none，设计文档 7.2）：主屏被切走时本机没有别的屏，窗口无处可丢，不做任何布局操作，
    // 也不需要快照。只记下上一次操作——拿回后报告扩展，让出后报告克隆——编排器的流程不用改。
    public class NoLayout : ILayout
    {
        LayoutMode mode = LayoutMode.Extend;

        public bool HasSnapshot { get { return true; } }
        public LayoutMode Mode { get { return mode; } }
        public void SaveSnapshot() { }
        // 只有一块屏，"仅主屏"与"扩展"没有区别
        public void Take(OwnedLayout target) { mode = LayoutMode.Extend; }
        public void Release() { mode = LayoutMode.Clone; }
        public string CurrentSignature() { return null; }
        public string SnapshotSignature() { return null; }
    }

    public static class Layouts
    {
        // 按配置的 layout 选实现：none = 不操作硬件；second = CCD（另有一块屏）
        public static ILayout Create(Config cfg, string snapshotPath, Log log)
        {
            if (cfg.LayoutNone) return new NoLayout();
            return new CcdLayout(snapshotPath, cfg, log);
        }
    }
}
