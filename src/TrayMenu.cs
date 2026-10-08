using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ScreenSwitch
{
    public enum MenuKind { Problem, TakeBack, Peer, Device, Separator, Extend, MainOnly, Settings, Quit }

    public class MenuEntry
    {
        public MenuKind Kind;
        public string Label = "";
        // 右边一列：快捷键或对号
        public string Right = "";
        public bool Enabled = true;
        public uint Code;
        public MenuEntry(MenuKind kind, string label, string right, bool enabled, uint code)
        {
            Kind = kind; Label = label ?? ""; Right = right ?? ""; Enabled = enabled; Code = code;
        }
    }

    // 右键菜单（docs/settings-ui-design.md 第 3 节），两端相同：
    //   [问题] / 拿回主屏 或 切给 <对方> / 每个设备 / — / 扩展屏幕 / 只用主屏 / — / 设置… / 退出
    // 没有状态行；文字左对齐，对号与快捷键在右边一列。
    public static class TrayMenu
    {
        public const string Check = "✓";

        public static List<MenuEntry> Build(Config cfg, bool owned, bool busy, OwnedLayout ownedLayout, IList<string> problems)
        {
            var l = new List<MenuEntry>();
            if (problems != null)
                foreach (string p in problems) l.Add(new MenuEntry(MenuKind.Problem, "⚠ " + p, "", true, 0));
            Separator(l);
            string peerKey = (cfg.PeerHotkey ?? "").Trim();
            if (!owned) l.Add(new MenuEntry(MenuKind.TakeBack, Tr.T("menu.take-back"), peerKey, !busy, 0));
            else if (cfg.PeerConfigured) l.Add(new MenuEntry(MenuKind.Peer, Tr.T("menu.give-to", cfg.PeerName), peerKey, !busy, 0));
            foreach (Device d in cfg.Devices)
                l.Add(new MenuEntry(MenuKind.Device, Tr.T("menu.give-to", d.Name), (d.Hotkey ?? "").Trim(), !busy, d.Code));
            Separator(l);
            if (!cfg.LayoutNone)
            {
                l.Add(new MenuEntry(MenuKind.Extend, Tr.T("menu.extend"), ownedLayout == OwnedLayout.Extend ? Check : "", true, 0));
                l.Add(new MenuEntry(MenuKind.MainOnly, Tr.T("menu.main-only"), ownedLayout == OwnedLayout.MainOnly ? Check : "", true, 0));
                Separator(l);
            }
            l.Add(new MenuEntry(MenuKind.Settings, Tr.T("menu.settings"), "", true, 0));
            l.Add(new MenuEntry(MenuKind.Quit, Tr.T("menu.quit"), "", true, 0));
            return l;
        }

        // 只在有内容、且上一项不是分隔线时加分隔线
        static void Separator(List<MenuEntry> l)
        {
            if (l.Count > 0 && l[l.Count - 1].Kind != MenuKind.Separator) l.Add(new MenuEntry(MenuKind.Separator, "", "", true, 0));
        }

        // 画到菜单上：不留左侧勾选/图标列，右边一列用 ShortcutKeyDisplayString
        public static void Fill(ContextMenuStrip menu, List<MenuEntry> entries, Action<MenuEntry> onClick)
        {
            var old = new List<ToolStripItem>();
            foreach (ToolStripItem it in menu.Items) old.Add(it);
            menu.Items.Clear();
            foreach (ToolStripItem it in old) it.Dispose();
            menu.ShowCheckMargin = false;
            menu.ShowImageMargin = false;
            foreach (MenuEntry entry in entries)
            {
                MenuEntry e = entry;
                if (e.Kind == MenuKind.Separator) { menu.Items.Add(new ToolStripSeparator()); continue; }
                var item = new ToolStripMenuItem(e.Label);
                item.Enabled = e.Enabled;
                item.ShowShortcutKeys = true;
                item.ShortcutKeyDisplayString = e.Right;
                item.Click += delegate { onClick(e); };
                menu.Items.Add(item);
            }
        }
    }
}
