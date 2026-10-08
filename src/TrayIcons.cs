using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ScreenSwitch
{
    // 托盘图标：一个显示器轮廓。占有主屏 = 蓝色实心带勾；未占有 = 灰色描边。
    public static class TrayIcons
    {
        static readonly Color Accent = Color.FromArgb(255, 47, 128, 237);
        static readonly Color Gray = Color.FromArgb(255, 150, 150, 150);

        public static Bitmap Draw(bool owned, int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 32f;
                Color col = owned ? Accent : Gray;
                using (var pen = new Pen(col, Math.Max(1.5f, 2.6f * s)))
                using (var brush = new SolidBrush(col))
                using (var white = new Pen(Color.White, Math.Max(1.5f, 2.4f * s)))
                using (var path = new GraphicsPath())
                {
                    pen.LineJoin = LineJoin.Round;
                    white.LineJoin = LineJoin.Round; white.StartCap = LineCap.Round; white.EndCap = LineCap.Round;
                    float x = 3 * s, y = 4 * s, w = 26 * s, h = 17 * s, r = 3 * s;
                    path.AddArc(x, y, 2 * r, 2 * r, 180, 90);
                    path.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
                    path.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
                    path.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
                    path.CloseFigure();
                    if (owned) g.FillPath(brush, path); else g.DrawPath(pen, path);
                    g.DrawLine(pen, 16 * s, 21 * s, 16 * s, 26 * s);
                    g.DrawLine(pen, 9 * s, 27.5f * s, 23 * s, 27.5f * s);
                    if (owned)
                    {
                        g.DrawLine(white, 11 * s, 12.5f * s, 14.5f * s, 16 * s);
                        g.DrawLine(white, 14.5f * s, 16 * s, 21 * s, 9.5f * s);
                    }
                }
            }
            return bmp;
        }

        // 返回的 Icon 与进程同生命周期，只创建两个，不回收句柄。
        public static Icon Make(bool owned, int size)
        {
            using (Bitmap bmp = Draw(owned, size))
            {
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
