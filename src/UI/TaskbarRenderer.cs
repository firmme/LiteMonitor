using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Helpers;
using System.Drawing.Text;

namespace LiteMonitor
{
    /// <summary>
    /// 任务栏渲染器（仅负责绘制，不再负责布局）
    /// </summary>
    public static class TaskbarRenderer
    {
        internal const TextFormatFlags TextFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        //private static readonly Settings _settings = Settings.Load();
        
        // 字体缓存 - 由本类完全拥有 (不进入 UIUtils 共享缓存，避免被 ClearBrushCache 释放)
        private static Font? _cachedFont = null;

        // 供布局测量复用，保证测量字体与渲染字体完全一致
        public static Font? CachedFont => _cachedFont;

        // 浅色主题
        private static readonly Color LABEL_LIGHT = Color.FromArgb(20, 20, 20);
        private static readonly Color SAFE_LIGHT = Color.FromArgb(0x00, 0x80, 0x40);
        private static readonly Color WARN_LIGHT = Color.FromArgb(0xB5, 0x75, 0x00);
        private static readonly Color CRIT_LIGHT = Color.FromArgb(0xC0, 0x30, 0x30);

        // 深色主题
        private static readonly Color LABEL_DARK = Color.White;
        private static readonly Color SAFE_DARK = Color.FromArgb(0x66, 0xFF, 0x99);
        private static readonly Color WARN_DARK = Color.FromArgb(0xFF, 0xD6, 0x66);
        private static readonly Color CRIT_DARK = Color.FromArgb(0xFF, 0x66, 0x66);

        // ★★★ [新增] 自定义颜色缓存 ★★★
        private static bool _useCustom = false;
        private static Color _cLabel, _cSafe, _cWarn, _cCrit;

        // ★★★ [新增] 极简的核心：手动刷新缓存 ★★★
        // 在 UIController 初始化或配置变更时调用它
        // dpi: 任务栏所在显示器的 DPI (<=0 时自动探测)，保证字体随 DPI 变化
        public static void ReloadStyle(Settings cfg, float dpi = 0)
        {     
            var s = cfg.GetStyle();

            if (dpi <= 0f) dpi = TaskbarWinHelper.GetTaskbarDpi();
            if (dpi <= 0f) dpi = 96f;

            // ★★★ 修复：先释放旧字体资源，防止 GDI 句柄泄漏 ★★★
            if (_cachedFont != null)
            {
                try { _cachedFont.Dispose(); } catch { }
                _cachedFont = null;
            }

            // 无论开关怎么变，这里拿到的永远是正确参数 (按目标 DPI 生成像素字体，由本类拥有)
            _cachedFont = UIUtils.CreateFont(s.Font, s.Size, s.Bold, dpi);

            // 颜色依然允许自定义
            _useCustom = cfg.TaskbarCustomStyle;
            if (_useCustom)
            {
                try {
                    _cLabel = ColorTranslator.FromHtml(cfg.TaskbarColorLabel);
                    _cSafe = ColorTranslator.FromHtml(cfg.TaskbarColorSafe);
                    _cWarn = ColorTranslator.FromHtml(cfg.TaskbarColorWarn);
                    _cCrit = ColorTranslator.FromHtml(cfg.TaskbarColorCrit);
                } catch {
                    // 容错：如果解析失败，回退到默认
                    _useCustom = false; 
                }
            }
        }

        public static void Render(Graphics g, List<Column> cols, bool light) // <--- 新的
        {
            // [防空策略] 万一还没人调用 ReloadStyle，就自己兜底初始化一次
            if (_cachedFont == null)
            {
                // 兜底：读磁盘配置（仅第一次）
                ReloadStyle(Settings.Load());
            }
            
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 使用传入的 light 参数，避免每次都查询系统主题瓠
            //bool light = IsSystemLight();

            foreach (var col in cols)
            {
                // ★★★ [新增]：如果只有 Top 没有 Bottom，强制使用全高绘制（居中）
                if (col.Top != null && col.Bottom == null && col.Bounds != Rectangle.Empty)
                {
                    DrawItem(g, col.Top, col.Bounds, light);
                    continue; // 处理完这个特殊情况直接跳过本次循环
                }

                if (col.BoundsTop != Rectangle.Empty && col.Top != null)
                    DrawItem(g, col.Top, col.BoundsTop, light);

                if (col.BoundsBottom != Rectangle.Empty && col.Bottom != null)
                    DrawItem(g, col.Bottom, col.BoundsBottom, light);
            }
        }

        private static void DrawItem(Graphics g, MetricItem item, Rectangle rc, bool light)
        {
            if (rc.Width <= 0 || rc.Height <= 0) return;
            // ★★★ 优化：直接使用缓存的 ShortLabel，避免每帧生成 Key 和查询字典 ★★★
            string label = item.ShortLabel;

            // ★★★ 修复：如果 ShortLabel 被显式设为空格或空，则视为隐藏标签 ★★★
            // 适用于 IP/Dashboard 文本，直接绘制 Value (左对齐)
            bool hideLabel = (string.IsNullOrEmpty(label) || label == " ");

            // 如果不是隐藏，且为空，则回退到 Label 或 Key
            if (!hideLabel)
            {
                if (string.IsNullOrEmpty(label)) label = item.Label;
                if (string.IsNullOrEmpty(label)) label = item.Key;
            }

            string value = item.GetFormattedText(true);

            // 直接使用缓存的字体，不再 new Font
            Font font = _cachedFont!;

            Color labelColor, valueColor;

            // ★★★ [修改] 颜色选择逻辑 ★★★
            if (_useCustom)
            {
                // 自定义模式：忽略系统明暗，强制使用自定义色
                labelColor = _cLabel;
                valueColor = GetCustomStateColor(item.CachedColorState);
            }
            else
            {
                // 原有模式
                labelColor = light ? LABEL_LIGHT : LABEL_DARK;
                valueColor = GetStateColor(item.CachedColorState, light);
            }

            // ★★★ 修复：如果开启了隐藏标签 (如 IP/Dashboard)，则仅绘制 Value (左对齐) ★★★
            if (hideLabel)
            {
                TextRenderer.DrawText(
                    g, value, font, rc, valueColor,
                    TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                );
                return;
            }

            // Label 左对齐
            TextRenderer.DrawText(
                g, label, font, rc, labelColor,
                TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            );

            // Value 右对齐
            TextRenderer.DrawText(
                g, value, font, rc, valueColor,
                TextFlags | TextFormatFlags.Right | TextFormatFlags.VerticalCenter
            );
        }
        // [新增] 辅助：根据状态快速获取颜色 (替代原来的 PickColor)
        private static Color GetStateColor(int state, bool light)
        {
            if (state == 2) return light ? CRIT_LIGHT : CRIT_DARK;
            if (state == 1) return light ? WARN_LIGHT : WARN_DARK;
            return light ? SAFE_LIGHT : SAFE_DARK;
        }

        // [新增] 辅助：自定义模式
        private static Color GetCustomStateColor(int state)
        {
            if (state == 2) return _cCrit;
            if (state == 1) return _cWarn;
            return _cSafe;
        }
    }
}
