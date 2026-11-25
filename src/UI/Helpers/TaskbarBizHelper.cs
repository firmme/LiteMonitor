using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using static LiteMonitor.src.UI.Helpers.TaskbarWinHelper;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 任务栏业务助手 (Business Helper)
    /// 职责：布局计算、位置定位、主题检测、菜单与双击逻辑
    /// </summary>
    public class TaskbarBizHelper
    {
        private readonly Settings _cfg;
        private readonly TaskbarWinHelper _winHelper;
        
        private Rectangle _taskbarRect = Rectangle.Empty;
        private int _taskbarHeight = 32;
        private IntPtr _hTaskbar = IntPtr.Zero;
        private IntPtr _hTray = IntPtr.Zero;
        private bool _isWin11;
        
        // 样式相关
        private bool _lastIsLightTheme = false;
        private Color _transparentKey = Color.Black;

        public int Height => _taskbarHeight;
        public Rectangle Rect => _taskbarRect;
        public Color TransparentKey => _transparentKey;
        public bool LastIsLightTheme => _lastIsLightTheme;

        public TaskbarBizHelper(Settings cfg, TaskbarWinHelper winHelper)
        {
            _cfg = cfg;
            _winHelper = winHelper;
            _isWin11 = Environment.OSVersion.Version >= new Version(10, 0, 22000);
        }

        // =================================================================
        // 样式与主题
        // =================================================================
        public void CheckTheme(bool force = false)
        {
            bool isLight = _winHelper.IsSystemLightTheme();
            if (!force && isLight == _lastIsLightTheme) return;
            _lastIsLightTheme = isLight;

            if (_cfg.TaskbarCustomStyle)
            {
                try 
                {
                    Color customColor = ColorTranslator.FromHtml(_cfg.TaskbarColorBg);
                    if (customColor.R == customColor.G && customColor.G == customColor.B)
                    {
                        int r = customColor.R;
                        int g = customColor.G;
                        int b = customColor.B;
                        if (b >= 255) b = 254; else b += 1;
                        _transparentKey = Color.FromArgb(r, g, b);
                    }
                    else
                    {
                        _transparentKey = customColor;
                    }
                } 
                catch { _transparentKey = Color.Black; }
            }
            else
            {
                if (isLight) _transparentKey = Color.FromArgb(210, 210, 211); 
                else _transparentKey = Color.FromArgb(40, 40, 41);       
            }

            _winHelper.ApplyLayeredStyle(_transparentKey, _cfg.TaskbarClickThrough);
        }

        // =================================================================
        // 布局与定位
        // =================================================================
        public void FindHandles()
        {
            var handles = _winHelper.FindHandles(_cfg.TaskbarMonitorDevice);
            if (handles.hTaskbar != _hTaskbar) _winHelper.RestoreTaskbar();
            _hTaskbar = handles.hTaskbar;
            _hTray = handles.hTray;
        }

        public bool UpdateTaskbarRect()
        {
            _taskbarRect = _winHelper.GetTaskbarRect(_hTaskbar);
            _taskbarHeight = _taskbarRect.Height;
            return !_taskbarRect.IsEmpty;
        }

        public bool IsVertical()
        {
            return _taskbarRect.Height > _taskbarRect.Width;
        }

        /// <summary>
        /// 获取当前任务栏所在显示器的 DPI
        /// </summary>
        public int GetTaskbarDpi()
        {
            if (_hTaskbar != IntPtr.Zero)
            {
                return TaskbarWinHelper.GetDpiForWindowHandle(_hTaskbar);
            }
            return TaskbarWinHelper.GetTaskbarDpi();
        }

        /// <summary>
        /// 判断当前任务栏是否有足够空间完整显示监控组件。
        /// 空间不足 (如低分辨率 / 任务栏可用区域太窄) 时返回 false。
        /// </summary>
        public bool HasEnoughSpace(int panelWidth, int panelHeight)
        {
            if (_hTaskbar == IntPtr.Zero || _taskbarRect.IsEmpty) return true;

            const int margin = 12;

            if (IsVertical())
            {
                int availHeight = _taskbarRect.Height;
                if (_hTray != IntPtr.Zero &&
                    _winHelper.GetWindowRectWrapper(_hTray, out Rectangle tray) &&
                    tray.Top > _taskbarRect.Top && tray.Top <= _taskbarRect.Bottom)
                {
                    availHeight = tray.Top - _taskbarRect.Top;
                }
                return panelHeight + margin + Math.Max(0, _cfg.TaskbarManualOffset) <= availHeight;
            }
            else
            {
                int availWidth = _taskbarRect.Width;
                if (_hTray != IntPtr.Zero &&
                    _winHelper.GetWindowRectWrapper(_hTray, out Rectangle tray) &&
                    tray.Left > _taskbarRect.Left && tray.Left <= _taskbarRect.Right)
                {
                    availWidth = tray.Left - _taskbarRect.Left;
                }
                return panelWidth + margin + Math.Max(0, _cfg.TaskbarManualOffset) <= availWidth;
            }
        }

        public bool UpdatePlacement(int panelWidth, int panelHeight)
        {
            if (_hTaskbar == IntPtr.Zero) return false;

            int leftScreen = _taskbarRect.Left;
            int topScreen;

            // ★★★ 垂直任务栏定位 ★★★
            if (IsVertical())
            {
                // 如果策略自带布局逻辑 (如 Win10 挤占模式)，直接委托处理
                if (_winHelper.UsesInternalLayout)
                {
                    // 注意：垂直模式下，Width 是固定的（任务栏宽度），Height 是 Monitor 高度
                    return _winHelper.SetPosition(_hTaskbar, leftScreen, _taskbarRect.Top, _taskbarRect.Width,
                        Math.Min(panelHeight, _taskbarRect.Height), _cfg.TaskbarManualOffset, _cfg.TaskbarAlignLeft);
                }

                // 尝试定位到托盘上方
                int bottomLimit = _taskbarRect.Bottom;
                
                if (_hTray != IntPtr.Zero && _winHelper.GetWindowRectWrapper(_hTray, out Rectangle trayRect))
                {
                    if (trayRect.Top >= _taskbarRect.Top && trayRect.Bottom <= _taskbarRect.Bottom)
                    {
                        bottomLimit = trayRect.Top;
                    }
                }

                topScreen = Math.Clamp(bottomLimit - panelHeight - _cfg.TaskbarManualOffset - 6,
                    _taskbarRect.Top, Math.Max(_taskbarRect.Top, _taskbarRect.Bottom - panelHeight));

                return _winHelper.SetPosition(_hTaskbar, leftScreen, topScreen, _taskbarRect.Width, Math.Min(panelHeight, _taskbarRect.Height));
            }

            // ★★★ 水平任务栏定位 ★★★

            Screen currentScreen = Screen.FromRectangle(_taskbarRect);
            if (currentScreen == null) currentScreen = Screen.PrimaryScreen;
            
            bool sysCentered = TaskbarWinHelper.IsCenterAligned();
            bool isPrimary = currentScreen.Primary;
            
            int rawWidgetWidth = TaskbarWinHelper.GetWidgetsWidth();      
            int manualOffset = _cfg.TaskbarManualOffset; 
            int leftModeTotalOffset = rawWidgetWidth + manualOffset;
            int sysRightAvoid = sysCentered ? 0 : rawWidgetWidth;
            int rightModeTotalOffset = sysRightAvoid + manualOffset;

            int timeWidth = _isWin11 ? (int)Math.Round(90.0 * GetTaskbarDpi() / 96.0) : 0;
            bool alignLeft = _cfg.TaskbarAlignLeft && sysCentered; 

            topScreen = _taskbarRect.Top;

            if (alignLeft)
            {
                int startX = _taskbarRect.Left + 6;
                if (leftModeTotalOffset > 0) startX += leftModeTotalOffset;
                leftScreen = startX;
            }
            else
            {
                // 如果策略自带布局逻辑 (如 Win10 挤占模式)，直接委托处理
                // 我们不需要计算 Right 偏移，位置由 Strategy 内部决定
                if (_winHelper.UsesInternalLayout)
                {
                    return _winHelper.SetPosition(_hTaskbar, leftScreen, topScreen, Math.Min(panelWidth, _taskbarRect.Width),
                        _taskbarHeight, _cfg.TaskbarManualOffset, _cfg.TaskbarAlignLeft);
                }

                if (isPrimary && _hTray != IntPtr.Zero && _winHelper.GetWindowRectWrapper(_hTray, out Rectangle tray) &&
                    tray.Left > _taskbarRect.Left && tray.Left <= _taskbarRect.Right)
                {
                    leftScreen = tray.Left - panelWidth - 6;
                    leftScreen -= rightModeTotalOffset;
                }
                else
                {
                    leftScreen = _taskbarRect.Right - panelWidth - 10;
                    leftScreen -= rightModeTotalOffset;
                    leftScreen -= timeWidth;
                }
            }

            leftScreen = Math.Clamp(leftScreen, _taskbarRect.Left, Math.Max(_taskbarRect.Left, _taskbarRect.Right - panelWidth));
            return _winHelper.SetPosition(_hTaskbar, leftScreen, topScreen, Math.Min(panelWidth, _taskbarRect.Width), _taskbarHeight);
        }

        public int BuildVerticalLayout(List<Column> cols)
        {
            var s = _cfg.GetStyle(); 
            
            int w = _taskbarRect.Width;

            float dpiScale = GetTaskbarDpi() / 96f;
            int itemHeight = (int)((s.Size * 1.5f + 6) * dpiScale);
            if (itemHeight < (int)(20 * dpiScale)) itemHeight = (int)(20 * dpiScale);

            int margin = Math.Clamp(s.Inner / 2, 0, (w - 1) / 2);
            int contentWidth = w - (margin * 2);

            int y = 0;
            foreach (var col in cols)
            {
                col.Bounds = Rectangle.Empty;
                col.BoundsTop = Rectangle.Empty;
                col.BoundsBottom = Rectangle.Empty;

                if (col.Top != null)
                {
                    col.BoundsTop = new Rectangle(margin, y, contentWidth, itemHeight);
                    y += itemHeight;
                }

                if (col.Bottom != null)
                {
                    col.BoundsBottom = new Rectangle(margin, y, contentWidth, itemHeight);
                    y += itemHeight;
                }

                if (col.Top != null && col.Bottom == null)
                {
                    col.Bounds = col.BoundsTop;
                }

                y += s.VOff;
            }
            return y;
        }

        // =================================================================
        // 交互动作
        // =================================================================
        public async void HandleDoubleClick(MainForm mainForm, UIController ui)
        {
            switch (_cfg.TaskbarDoubleClickAction)
            {
                case 1: try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true }); } catch { } break;
                case 2: 
                    foreach (Form f in Application.OpenForms) { if (f is SettingsForm) { f.Activate(); return; } }
                    new SettingsForm(_cfg, ui, mainForm).Show(); 
                    break;
                case 3: 
                    foreach (Form f in Application.OpenForms) { if (f is TrafficHistoryForm) { f.Activate(); return; } }
                    new TrafficHistoryForm(_cfg).Show(); 
                    break;
                case 4: 
                    try { using (var form = new CleanMemoryForm()) await form.StartCleaningAsync(); } catch { } 
                    break;
                case 5:
                    Core.Actions.WebActions.OpenWebMonitor(_cfg);
                    break;
                case 0: 
                default:
                    if (mainForm.Visible) mainForm.HideMainWindow(); else mainForm.ShowMainWindow();
                    break;
            }
        }
    }
}
