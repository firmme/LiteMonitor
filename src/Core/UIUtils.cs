using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// LiteMonitor UI Utilities (Refactored)
    /// Logic delegated to MetricUtils.
    /// This class now focuses on GDI+ Rendering helpers and Resource Management.
    /// </summary>
    public static class UIUtils
    {
        // ============================================================
        // 1. String Interning (Memory Optimization)
        // ============================================================
        private static readonly Dictionary<string, string> _stringPool = new(StringComparer.Ordinal);
        private static readonly object _poolLock = new object();

        public static string Intern(string str)
        {
            if (string.IsNullOrEmpty(str)) return string.Empty;
            lock (_poolLock)
            {
                if (_stringPool.TryGetValue(str, out var pooled)) return pooled;
                _stringPool[str] = str;
                return str;
            }
        }

        public static void ClearStringPool()
        {
            lock (_poolLock) _stringPool.Clear();
        }

        // ============================================================
        // 2. DPI Scaling
        // ============================================================
        public static float ScaleFactor { get; set; } = 1.0f;

        public static int S(int px) => (int)(px * ScaleFactor);
        public static float S(float px) => px * ScaleFactor;
        public static Size S(Size size) => new Size(S(size.Width), S(size.Height));
        public static Padding S(Padding p) => new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));

        // ============================================================
        // 3. GDI+ Resource Cache (Brushes & Fonts)
        // ============================================================
        private static readonly Dictionary<string, SolidBrush> _brushCache = new(16);
        private static readonly Dictionary<string, Pen> _penCache = new(16);
        private static readonly Dictionary<string, Font> _fontCache = new(16);
        private static readonly object _brushLock = new object();
        private const int MAX_BRUSH_CACHE = 32;

        public static Pen GetPen(Color color, float width = 1.0f)
        {
            string key = $"{color.ToArgb()}_{width:F1}";
            lock (_brushLock)
            {
                if (!_penCache.TryGetValue(key, out var pen))
                {
                    if (_penCache.Count >= MAX_BRUSH_CACHE)
                    {
                        foreach (var p in _penCache.Values) p.Dispose();
                        _penCache.Clear();
                    }
                    pen = new Pen(color, width);
                    _penCache[key] = pen;
                }
                return pen;
            }
        }

        public static SolidBrush GetBrush(string color)
        {
            if (string.IsNullOrEmpty(color)) return (SolidBrush)Brushes.Transparent;

            lock (_brushLock)
            {
                if (!_brushCache.TryGetValue(color, out var br))
                {
                    // Cache eviction policy
                    if (_brushCache.Count >= MAX_BRUSH_CACHE)
                    {
                        var keysToRemove = _brushCache.Keys.Take(_brushCache.Count / 2).ToList();
                        foreach (var k in keysToRemove)
                        {
                            if (_brushCache.TryGetValue(k, out var oldBrush))
                            {
                                oldBrush.Dispose();
                                _brushCache.Remove(k);
                            }
                        }
                    }

                    br = new SolidBrush(ThemeManager.ParseColor(color));
                    _brushCache[color] = br;
                }
                return br;
            }
        }

        public static Font GetFont(string familyName, float size, bool bold)
        {
            string key = $"{familyName}_{size}_{bold}";
            lock (_brushLock)
            {
                if (!_fontCache.TryGetValue(key, out var font))
                {
                    try 
                    {
                        var style = bold ? FontStyle.Bold : FontStyle.Regular;
                        font = new Font(familyName, size, style);
                    }
                    catch
                    {
                        font = new Font(SystemFonts.DefaultFont.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular);
                    }
                    _fontCache[key] = font;
                }
                return font;
            }
        }

        // ============================================================
        // 3.1 DPI 感知字体 (用于任务栏组件等需要跟随显示器 DPI 的场景)
        // ============================================================
        /// <summary>
        /// 按目标显示器 DPI 创建一个"调用方拥有"的字体 (不进入共享缓存)。
        /// size 为磅值 (pt)，内部换算为像素并使用 GraphicsUnit.Pixel，
        /// 这样即使进程为 SystemAware，也能保证字体大小与目标显示器一致。
        /// 注意：调用方负责在不再使用时 Dispose，避免与 ClearBrushCache 冲突导致使用已释放字体。
        /// </summary>
        public static Font CreateFont(string familyName, float sizePoints, bool bold, float dpi)
        {
            if (dpi <= 0f) dpi = 96f;

            // pt -> px (96 DPI 下 1pt = 96/72 px)
            float pixel = sizePoints * dpi / 72f;
            if (pixel < 1f) pixel = 1f;

            var style = bold ? FontStyle.Bold : FontStyle.Regular;
            try
            {
                return new Font(familyName, pixel, style, GraphicsUnit.Pixel);
            }
            catch
            {
                return new Font(SystemFonts.DefaultFont.FontFamily, pixel, style, GraphicsUnit.Pixel);
            }
        }

        public static void ClearFontCache()
        {
            lock (_brushLock)
            {
                foreach (var f in _fontCache.Values)
                {
                    try { f.Dispose(); } catch { }
                }
                _fontCache.Clear();
            }
        }

        // ============================================================
        // 3.2 递归开启双缓冲 (用于设置页等自定义绘制容器，消除滚动时子控件重绘撕裂)
        // ============================================================
        private static readonly System.Reflection.PropertyInfo? _doubleBufferedProp =
            typeof(System.Windows.Forms.Control).GetProperty(
                "DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        public static void EnableDoubleBuffering(System.Windows.Forms.Control? root)
        {
            if (root == null || _doubleBufferedProp == null) return;

            void Apply(System.Windows.Forms.Control c)
            {
                try { _doubleBufferedProp.SetValue(c, true, null); } catch { }
                foreach (System.Windows.Forms.Control child in c.Controls) Apply(child);
            }

            Apply(root);
        }

        public static void ClearBrushCache()
        {
            lock (_brushLock)
            {
                foreach (var b in _brushCache.Values) b.Dispose();
                _brushCache.Clear();

                foreach (var p in _penCache.Values) p.Dispose();
                _penCache.Clear();
                
                foreach (var f in _fontCache.Values) f.Dispose();
                _fontCache.Clear();
            }
        }

        // ============================================================
        // 4. Theme Helpers
        // ============================================================

        public static Color GetStateColor(int state, Theme t, bool isValueText = true)
        {
            if (state == MetricUtils.STATE_CRIT) return ThemeManager.ParseColor(isValueText ? t.Color.ValueCrit : t.Color.BarHigh);
            if (state == MetricUtils.STATE_WARN) return ThemeManager.ParseColor(isValueText ? t.Color.ValueWarn : t.Color.BarMid);
            return ThemeManager.ParseColor(t.Color.ValueSafe);
        }

        // ============================================================
        // 5. Drawing Helpers
        // ============================================================

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return p;

            if (radius <= 0)
            {
                p.AddRectangle(r);
                return p;
            }

            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRoundRect(Graphics g, Rectangle r, int radius, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using var brush = new SolidBrush(c);
            using var path = RoundRect(r, radius);
            g.FillPath(brush, path);
        }

        public static void DrawBar(Graphics g, MetricItem item, Theme t)
        {
            if (item.BarRect.Width <= 0 || item.BarRect.Height <= 0) return;

            // Background
            using (var bgPath = RoundRect(item.BarRect, item.BarRect.Height / 2))
            {
                g.FillPath(GetBrush(t.Color.BarBackground), bgPath);
            }

            // 2. Value Bar
            // [Refactor] CachedPercent already includes visual correction (min 5%, max 100%) via MetricUtils.GetProgressValue
            double percent = item.CachedPercent; 
            
            int w = (int)(item.BarRect.Width * percent);
            if (w < 1 && percent > 0) w = 1; // 绝对防御：只要有百分比，至少画1像素

            // Color
            Color barColor = GetStateColor(item.CachedColorState, t, false);

            if (w > 0)
            {
                Rectangle bar = new Rectangle(item.BarRect.X, item.BarRect.Y, w, item.BarRect.Height);
                if (bar.Width > 0 && bar.Height > 0)
                {
                    using (var fgPath = RoundRect(bar, bar.Height / 2))
                    using (var brush = new SolidBrush(barColor))
                    {
                        g.FillPath(brush, fgPath);
                    }
                }
            }
        }
    }
}
