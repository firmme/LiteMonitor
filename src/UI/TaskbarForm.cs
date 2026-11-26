using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor
{
    public class TaskbarForm : Form
    {
        private readonly Settings _cfg;
        private readonly UIController _ui;
        private readonly MainForm _mainForm;
        private readonly System.Windows.Forms.Timer _timer = new();

        // ★★★ 双助手架构 ★★★
        private readonly TaskbarWinHelper _winHelper;
        private readonly TaskbarBizHelper _bizHelper;
        
        private HorizontalLayout _layout = null!;
        private List<Column>? _cols;
        private ContextMenuStrip? _currentMenu;
        private long _nextHandleLookup;
        private string _lastLayoutSignature = "";
        private readonly TaskbarTooltipHelper _tooltipHelper;

        // ★★★ DPI 跟踪：任务栏所在显示器 DPI 变化时刷新字体与布局 ★★★
        private int _taskbarDpi;
        private bool _styleDirty = true;
        internal bool IsNativeWindowValid => !IsDisposed && IsHandleCreated && IsWindow(Handle);
        
        // 公开属性
        public string TargetDevice { get; private set; } = "";

        // 判断菜单是否打开
        public bool IsMenuOpen => _currentMenu != null && !_currentMenu.IsDisposed && _currentMenu.Visible;
        
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_LBUTTONDBLCLK = 0x0203;

        public TaskbarForm(Settings cfg, UIController ui, MainForm mainForm)
        {
            _cfg = cfg;
            _ui = ui;
            _mainForm = mainForm;
            TargetDevice = _cfg.TaskbarMonitorDevice;

            // 初始化组件
            _winHelper = new TaskbarWinHelper(this);
            _bizHelper = new TaskbarBizHelper(_cfg, _winHelper);

            // 窗体属性
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            ControlBox = false;
            TopMost = false;
            DoubleBuffered = true;

            _tooltipHelper = new TaskbarTooltipHelper(this, _cfg, _ui);
            ReloadLayout();

            _timer.Tick += (_, __) => Tick();
            _timer.Start();
        }

        public void ReloadLayout()
        {
            _timer.Interval = Math.Max(_cfg.RefreshMs, 60);
            _nextHandleLookup = 0;
            _styleDirty = true;
            _bizHelper.CheckTheme(true);
            _tooltipHelper.ReloadMode();
            Tick();
        }

        protected override bool ShowWithoutActivation => true;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _winHelper?.RestoreTaskbar();
                _timer.Stop();
                _timer.Dispose();
                _currentMenu?.Dispose();
                _tooltipHelper?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            // [Fix] 兼容性修复：在 Win11 25H2 + StartAllBack 环境下，
            // 右键事件会穿透到原生任务栏。
            // 因此不再区分系统版本，统一拦截右键按下和抬起消息。
            if (m.Msg == WM_RBUTTONDOWN)
            {
                return; // 吞掉按下事件，防止穿透
            }

            if (m.Msg == WM_RBUTTONUP)
            {
                this.BeginInvoke(new Action(ShowContextMenu));
                return; 
            }

            // [Fix] 强制拦截双击事件
            // 当悬浮窗(Tooltip)显示时，WinForms 的标准双击事件可能因为焦点/激活状态的微妙变化而失效。
            // 这里直接在消息层处理 WM_LBUTTONDBLCLK，确保双击动作始终能被触发。
            if (m.Msg == WM_LBUTTONDBLCLK)
            {
                _bizHelper.HandleDoubleClick(_mainForm, _ui);
                return;
            }

            base.WndProc(ref m);
        }

        private void ShowContextMenu()
        {
            if (_currentMenu != null)
            {
                _currentMenu.Dispose();
                _currentMenu = null;
            }

            _currentMenu = MenuManager.Build(_mainForm, _cfg, _ui, "Taskbar");
            
            TaskbarWinHelper.ActivateWindow(this.Handle);
            _currentMenu.Show(Cursor.Position);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left)
            {
                _bizHelper.HandleDoubleClick(_mainForm, _ui);
            }
        }

        private void Tick()
        {
            // 显示器或托盘可以在原来的任务栏句柄仍有效时发生变化。
            long now = Environment.TickCount64;
            if (now >= _nextHandleLookup)
            {
                _bizHelper.FindHandles();
                _nextHandleLookup = now + 2000;
            }

            if (!_bizHelper.UpdateTaskbarRect())
            {
                HideTaskbarPanel();
                return;
            }

            int currentDpi = _bizHelper.GetTaskbarDpi();
            if (currentDpi != _taskbarDpi || _styleDirty)
            {
                _taskbarDpi = currentDpi;
                _styleDirty = false;
                TaskbarRenderer.ReloadStyle(_cfg, currentDpi);
                _layout = new HorizontalLayout(ThemeManager.Current, Width, LayoutMode.Taskbar, _cfg, currentDpi);
                _lastLayoutSignature = "";
            }

            if (Math.Abs(Environment.TickCount) % 5000 < _cfg.RefreshMs) _bizHelper.CheckTheme();

            // [Fix Part 1] 防空数据保护
            // 使用临时变量接收，先判断数据有效性，再赋值给成员变量 _cols
            // 防止在 UI 重建期间(RebuildLayout)获取到空列表导致任务栏闪烁或清空
            var nextCols = _ui.GetTaskbarColumns();
            if (nextCols == null || nextCols.Count == 0) return;
            
            _cols = nextCols; // 确认有效后再更新引用

            // [Fix Part 2] 布局变更检测
            bool isVertical = _bizHelper.IsVertical();
            int panelHeight = _bizHelper.Height;
            if (isVertical)
            {
                // 垂直模式逻辑简单且无测量开销，直接重算即可
                panelHeight = _bizHelper.BuildVerticalLayout(_cols);
                _lastLayoutSignature = "vertical";
            }
            else
            {
                // [优化] 智能判断更新条件
                // 1. 必须检查：如果列表是新生成的（坐标还没算过，Bounds为空），必须重算！
                // 这解决了“主界面显隐导致任务栏消失”的问题
                bool isUninitialized = (_cols.Count > 0 && _cols[0].Bounds.IsEmpty);

                // 2. 常规检查：如果内容长度/结构变了（签名变了），也要重算
                string currentSig = _layout.GetLayoutSignature(_cols) + "_" + _bizHelper.Height;
                bool isContentChanged = (currentSig != _lastLayoutSignature);

                if (isUninitialized || isContentChanged)
                {
                    _layout.Build(_cols, _bizHelper.Height);
                    _lastLayoutSignature = currentSig;
                }
            }
            
            int panelWidth = isVertical ? _bizHelper.Rect.Width : _layout.PanelWidth;
            if (_cfg.TaskbarHideOnOverflow && !_bizHelper.HasEnoughSpace(panelWidth, panelHeight))
            {
                HideTaskbarPanel();
                return;
            }

            if (!_bizHelper.UpdatePlacement(panelWidth, panelHeight))
            {
                HideTaskbarPanel();
                return;
            }
            if (!Visible) Show();
            
            _tooltipHelper.UpdateContent();

            Invalidate();
        }

        private void HideTaskbarPanel()
        {
            if (!Visible) return;
            Hide();
            _winHelper.RestoreTaskbar();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(_bizHelper.TransparentKey);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // ★ 调试验证用：如果消失时出现了一个红块，说明 OnPaint 被调用但 _cols 为空
            // e.Graphics.FillRectangle(Brushes.Red, 0, 0, 20, 20); 

            if (_cols == null) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            TaskbarRenderer.Render(g, _cols, _bizHelper.LastIsLightTheme);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE (防止点击激活窗口，避免抢占焦点)
                if (_cfg != null && _cfg.TaskbarClickThrough)
                {
                    cp.ExStyle |= WS_EX_TRANSPARENT;
                }
                return cp;
            }
        }
    }
}
