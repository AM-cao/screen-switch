namespace ScreenSwitch
{
    // 重新加载配置的时机：托盘正在切换时先记下，切换结束再做，免得中途替换运行核心
    public class ReloadGate
    {
        bool pending;

        // 返回 true：现在就重新加载
        public bool Request(bool busy)
        {
            if (busy) { pending = true; return false; }
            pending = false;
            return true;
        }

        // 切换结束时调用：之前有推迟的请求就返回 true（只一次）
        public bool OnIdle()
        {
            if (!pending) return false;
            pending = false;
            return true;
        }
    }
}
