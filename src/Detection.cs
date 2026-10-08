using System;
using System.Collections.Generic;

namespace ScreenSwitch
{
    public class DetectResult
    {
        // null = 成功
        public string Error;
        public List<SetupMonitor> Monitors = new List<SetupMonitor>();
        // 应答 DDC/CI 的显示器（可以作主屏的候选）
        public List<SetupMonitor> Candidates = new List<SetupMonitor>();
        public SetupMonitor Main;
        public string MainEdid = "";
        public uint SelfCode;
        public int Connected;
        public string Layout = "second";
        // 另一块屏的名字（没有为空串）
        public string OtherScreen = "";
    }

    // 从探测结果选主屏，算本机编码与屏幕组合（docs/settings-ui-design.md 第 4 节第 2 页、第 7 节 detect）
    public static class Detection
    {
        public static DetectResult Choose(List<SetupMonitor> mons, int connected, string mainModel)
        {
            var r = new DetectResult();
            r.Monitors = mons ?? new List<SetupMonitor>();
            r.Connected = Math.Max(connected, 1);
            var ids = new List<string>();
            foreach (SetupMonitor m in r.Monitors)
            {
                ids.Add(m.DeviceId);
                if (m.Ddc) r.Candidates.Add(m);
            }
            if (r.Candidates.Count == 0) { r.Error = Tr.T("detect.err.no-ddc"); return r; }
            if (r.Connected > 2) { r.Error = Tr.T("detect.err.three", r.Connected); return r; }
            if (!string.IsNullOrEmpty(mainModel))
            {
                foreach (SetupMonitor m in r.Candidates)
                    if (string.Equals(Setup.ModelId(m.DeviceId), mainModel, StringComparison.OrdinalIgnoreCase)
                        || m.DeviceId.IndexOf(mainModel, StringComparison.OrdinalIgnoreCase) >= 0) { r.Main = m; break; }
                if (r.Main == null) { r.Error = Tr.T("detect.err.no-match", mainModel); return r; }
            }
            else if (r.Candidates.Count == 1) r.Main = r.Candidates[0];
            else { r.Error = Tr.T("detect.err.choose"); return r; }
            r.MainEdid = Setup.MainEdidFor(r.Main.DeviceId, ids);
            r.SelfCode = r.Main.Input & 0xFF;
            r.Layout = r.Connected >= 2 ? "second" : "none";
            foreach (SetupMonitor m in r.Monitors)
                if (m != r.Main) { r.OtherScreen = (m.Name ?? "").Trim().Length > 0 ? m.Name.Trim() : Setup.ModelId(m.DeviceId); break; }
            if (r.Layout == "none") r.OtherScreen = "";
            return r;
        }
    }
}
