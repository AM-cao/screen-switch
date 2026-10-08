using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace ScreenSwitch
{
    // 托盘菜单里的偏好（安装目录下 prefs.json）：拿回后的布局、是否自动记住屏幕排列。
    // 由程序写入，与手工编辑的 config.json 分开。
    public class Prefs
    {
        public OwnedLayout OwnedLayout = OwnedLayout.Extend;
        public bool AutoSaveLayout = true;

        // 文件里的取值：extend / mainOnly
        public static string Name(OwnedLayout v) { return v == OwnedLayout.MainOnly ? "mainOnly" : "extend"; }

        // 文件缺失或损坏时返回默认值，不抛；未知取值、类型不对的项按默认。
        public static Prefs Load(string path)
        {
            var p = new Prefs();
            try
            {
                if (!File.Exists(path)) return p;
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                if (root == null) return p;
                object v;
                if (root.TryGetValue("ownedLayout", out v) && v is string && (string)v == "mainOnly") p.OwnedLayout = OwnedLayout.MainOnly;
                if (root.TryGetValue("autoSaveLayout", out v) && v is bool) p.AutoSaveLayout = (bool)v;
                return p;
            }
            catch (Exception) { return new Prefs(); }
        }

        // 先写 .tmp 再覆盖，避免写到一半留下损坏的文件。
        public void Save(string path)
        {
            var d = new Dictionary<string, object>();
            d["ownedLayout"] = Name(OwnedLayout);
            d["autoSaveLayout"] = AutoSaveLayout;
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(d));
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }
    }
}
