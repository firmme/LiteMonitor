using Microsoft.Win32;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI;
using LiteMonitor.src.UI.Helpers;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class MainForm : Form
    {
        private readonly Settings _cfg = Settings.Load();
        private UIController? _ui;
        
        // ★★★ 双助手架构 ★★★
        private readonly MainFormWinHelper _winHelper;
        private readonly MainFormBizHelper _bizHelper;
        private readonly int _wmTaskbarCreated;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_WTSSESSION_CHANGE = 0x02B1;
        private readonly System.Windows.Forms.Timer _displayRefreshTimer = new();
        private IntPtr _sessionNotificationHandle;
        private bool _displayRefreshPending;
        private bool _taskbarRestartPending;

        [System.Runtime.InteropServices.DllImport("wtsapi32.dll", ExactSpelling = true)]
        private static extern bool WTSRegisterSessionNotification(IntPtr handle, int flags);

        [System.Runtime.InteropServices.DllImport("wtsapi32.dll", ExactSpelling = true)]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr handle);

        private Point _dragOffset;
        private bool _uiDragging = false;

        // 防止 Win11 自动隐藏无边框 + 无任务栏窗口
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
                cp.ExStyle &= ~0x00040000; // WS_EX_APPWINDOW
                if (_cfg != null && _cfg.TopMost)
                {
                    cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST，防止句柄重建后丢失置顶样式
                }
                
                // [Fix] 启动时应用鼠标穿透配置，防止因句柄重建导致样式丢失
                if (_cfg != null && _cfg.ClickThrough)
                {
                    cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT
                    cp.ExStyle |= 0x80000; // WS_EX_LAYERED (必须配合才能实现完整穿透)
                }

                return cp;
            }
        }

        // ========== 代理方法 (保持兼容性) ==========
        public void SetClickThrough(bool enable) => _winHelper.SetClickThrough(enable);
        public void InitAutoHideTimer() => _bizHelper.StartTimer();
        public void StopAutoHideTimer() => _bizHelper.StopTimer();
        public void HideTrayIcon() => _bizHelper.SetTrayVisible(false);
        public void ShowTrayIcon() => _bizHelper.SetTrayVisible(true);
        public void RebuildMenus() => _bizHelper.RebuildMenus();
        public void ShowNotification(string title, string text, ToolTipIcon icon) => _bizHelper.ShowNotification(title, text, icon);
        
        // 供 Helper 调用
        public void ToggleLayoutMode() => _bizHelper.ToggleLayoutMode();
        public void EnsureVisibleAndSavePos() => _bizHelper.SavePos();
        public void ApplyRoundedCorners() => _winHelper.ApplyRoundedCorners();
        public void RefreshTopMost(bool forceReinsert = false) => _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert);
        
        // 供外部调用
        public void OpenTaskManager() => _bizHelper.OpenTaskManager();
        public void OpenSettings() => _bizHelper.OpenSettings();
        public void OpenTrafficHistory() => _bizHelper.OpenTrafficHistory();
        public void CleanMemory() => _bizHelper.CleanMemory();

        // ==== 任务栏显示 ====
        private TaskbarForm? _taskbar;

        public void ToggleTaskbar(bool show)
        {
            if (show)
            {
                if (_taskbar != null && !_taskbar.IsDisposed)
                {
                    if (_taskbar.TargetDevice != _cfg.TaskbarMonitorDevice)
                    {
                        _taskbar.Close();
                        _taskbar.Dispose();
                        _taskbar = null;
                    }
                }

                if (_taskbar == null || _taskbar.IsDisposed)
                {
                    if (_ui != null)
                    {
                        _taskbar = new TaskbarForm(_cfg, _ui, this);
                    }
                }
                else
                {
                    _taskbar.ReloadLayout();
                }
            }
            else
            {
                if (_taskbar != null)
                {
                    _taskbar.Close();
                    _taskbar.Dispose();
                    _taskbar = null;
                }
            }
        }

        internal void ReloadTaskbarLayout()
        {
            if (_taskbar is { IsDisposed: false }) _taskbar.ReloadLayout();
        }

        // ========== 构造函数 ==========
        public MainForm()
        {
            _displayRefreshTimer.Tick += (_, _) => RefreshDisplayLayout();
            // 语言加载
            if (string.IsNullOrEmpty(_cfg.Language))
            {
                string sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLower();
                string langPath = Path.Combine(AppContext.BaseDirectory, "resources/lang", $"{sysLang}.json");
                _cfg.Language = File.Exists(langPath) ? sysLang : "en";
            }
            LanguageManager.Load(_cfg.Language);
            _cfg.SyncToLanguage();

            // 1. 初始化业务
            // ★★★ Fix: 初始化全局 DPI 缩放系数，防止未打开设置面板时弹窗排版异常 ★★★
            UIUtils.ScaleFactor = this.DeviceDpi / 96f;

            TrafficLogger.Load();
            HardwareHistoryLogger.Load();
            src.Plugins.PluginManager.Instance.LoadPlugins(Path.Combine(AppContext.BaseDirectory, "resources", "plugins"));
            src.Plugins.PluginManager.Instance.Start();
            _ui = new UIController(_cfg, this);
            new src.WebServer.LiteWebServer(_cfg);

            // 5. 设置背景色 (这是关键！解耦时漏掉了这行，导致背景是系统默认色而非透明或皮肤色)
            BackColor = ThemeManager.ParseColor(ThemeManager.Current.Color.Background);

            // 2. 初始化双助手
            _winHelper = new MainFormWinHelper(this);
            
            //资源管理器重启监听
            _wmTaskbarCreated = MainFormWinHelper.RegisterTaskbarCreatedMessage();

            // ★★★ 关键修复：补全 SetStyle 调用，开启透明支持 ★★★
            // 原始代码中这里调用了 SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            // 解耦时漏掉了这一行，导致背景无法透明，显示为黑色或系统默认色
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _winHelper.InitializeStyle(_cfg.TopMost, _cfg.ClickThrough);

            // 原始代码还原：这里需要手动设置 Opacity = 0，
            // 但是要在构造函数里设置，和原始代码保持一致的位置
            this.Opacity = 0; 

            _bizHelper = new MainFormBizHelper(this, _cfg, _ui, _winHelper);
            _bizHelper.Initialize();

            // === 渐入透明度 (还原原始代码逻辑) ===
            // 原始代码是在构造函数末尾启动 Task
            // 之前解耦时移到了 OnShown 里，这可能导致时序差异（OnShown 之前会有一瞬间的默认绘制）
            _winHelper.StartFadeIn(_cfg.Opacity);

            // 3. 事件绑定
            BindEvents();
        }

        private void BindEvents()
        {
            // 拖拽
            MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _ui?.SetDragging(true);
                    _uiDragging = true;
                    _bizHelper.IsDragging = true;
                    _dragOffset = e.Location;
                }
            };
            MouseMove += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (Math.Abs(e.X - _dragOffset.X) + Math.Abs(e.Y - _dragOffset.Y) < 1) return;
                    Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);
                }
            };
            MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _ui?.SetDragging(false);
                    _uiDragging = false;
                    _bizHelper.IsDragging = false;
                    _bizHelper.ClampToScreen(); 
                    _bizHelper.SavePos();
                }
            };

            // 双击
            this.DoubleClick += (_, __) => _bizHelper.HandleDoubleClick();
            
            // DPI / Resize
            this.Resize += (_, __) => _winHelper.ApplyRoundedCorners();
            this.VisibleChanged += (_, __) => { if (Visible) _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true); };
            this.HandleCreated += (_, __) => _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true);
        }

        public void ShowMainWindow()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
            _cfg.HideMainForm = false;
            _cfg.Save();

            _bizHelper.ForceShow();
            _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true);
            _bizHelper.RebuildMenus();
        }

        public void HideMainWindow()
        {
            this.Hide();
            _cfg.HideMainForm = true;
            _cfg.Save();
            _bizHelper.RebuildMenus();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == _wmTaskbarCreated && _wmTaskbarCreated != 0 && _cfg.ShowTaskbar)
            {
                _taskbarRestartPending = true;
                ScheduleDisplayRefresh(3000);
            }
            else if (m.Msg == WM_DISPLAYCHANGE)
            {
                ScheduleDisplayRefresh(500);
            }
            else if (m.Msg == WM_WTSSESSION_CHANGE && (SessionSwitchReason)m.WParam.ToInt32() is
                SessionSwitchReason.SessionLogon or SessionSwitchReason.SessionUnlock or
                SessionSwitchReason.ConsoleConnect or SessionSwitchReason.ConsoleDisconnect or
                SessionSwitchReason.RemoteConnect or SessionSwitchReason.RemoteDisconnect)
            {
                ScheduleDisplayRefresh(1000);
            }

            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterSessionNotifications();
            if (_displayRefreshPending) ScheduleDisplayRefresh(500);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _displayRefreshTimer.Stop();
            if (_sessionNotificationHandle != IntPtr.Zero)
            {
                WTSUnRegisterSessionNotification(_sessionNotificationHandle);
                _sessionNotificationHandle = IntPtr.Zero;
            }
            base.OnHandleDestroyed(e);
        }

        private void RegisterSessionNotifications()
        {
            const int notifyForThisSession = 0;
            if (_sessionNotificationHandle != IntPtr.Zero || !IsHandleCreated) return;
            if (WTSRegisterSessionNotification(Handle, notifyForThisSession))
                _sessionNotificationHandle = Handle;
            else if (!_displayRefreshTimer.Enabled)
            {
                // 开机自启时，远程桌面服务可能尚未就绪。
                _displayRefreshTimer.Interval = 2000;
                _displayRefreshTimer.Start();
            }
        }

        private void ScheduleDisplayRefresh(int delay)
        {
            _displayRefreshPending = true;
            _displayRefreshTimer.Stop();
            _displayRefreshTimer.Interval = _taskbarRestartPending ? 3000 : delay;
            _displayRefreshTimer.Start();
        }

        private void RefreshDisplayLayout()
        {
            _displayRefreshTimer.Stop();
            RegisterSessionNotifications();
            if (!_displayRefreshPending) return;

            _displayRefreshPending = false;
            bool restartTaskbar = _taskbarRestartPending || _taskbar is { IsNativeWindowValid: false };
            _taskbarRestartPending = false;
            if (restartTaskbar) ToggleTaskbar(false);

            _bizHelper?.RestorePos();
            UIUtils.ScaleFactor = DeviceDpi / 96f;
            _ui?.ApplyTheme(_cfg.Skin, retainData: true);
            _bizHelper?.ClampToScreen();
            if (_cfg.ShowTaskbar && (_taskbar == null || _taskbar.IsDisposed)) ToggleTaskbar(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            _ui?.Render(e.Graphics);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            UIUtils.ScaleFactor = e.DeviceDpiNew / 96f;
            _ui?.ApplyTheme(_cfg.Skin, retainData: true);
            _winHelper?.ApplyRoundedCorners();
            this.Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            
            // 恢复可见性
            if (_cfg.HideMainForm) this.Hide();
            
            this.Update();
            
            // 恢复位置
            _bizHelper.RestorePos();

            // 确保渲染尺寸正确 (横屏模式)
            if (_cfg.HorizontalMode && _ui != null)
            {
                this.Size = new Size(this.Width, this.Height);
            }
            
            // 移除了 StartFadeIn 调用，因为它已经还原回构造函数了
            _winHelper.ApplyRoundedCorners();
            _bizHelper.KeepVisible(3.0); // 启动保护期

            if (_cfg.ShowTaskbar) ToggleTaskbar(true);

            // 启动 WebServer
            if (_cfg.WebServerEnabled)
            {
                if (src.WebServer.LiteWebServer.Instance?.Start(out string err) == false)
                {
                     ShowNotification("WebServer Error", 
                         (_cfg.Language == "zh" ? "Web服务启动失败: " : "Web Server Failed: ") + err, 
                         ToolTipIcon.Error);
                }
            }
            // 这样既检查了驱动，也检查了更新，以及置顶 透明度 穿透 等，而且时机完美（窗口显示后）
            if (_bizHelper != null)
            {
                 _ = _bizHelper.RunStartupChecksAsync();
            }
            // [Fix] 强制置顶刷新，增加重试机制确保在某些系统环境下依然生效
            if (_cfg.TopMost)
            {
                this.BeginInvoke(new Action(async () =>
                {
                    await Task.Delay(3000);
                    _winHelper.RefreshTopMost(true, forceReinsert: true);
                }));
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _displayRefreshTimer.Stop();
            ToggleTaskbar(false);
            _cfg.Save(); 
            TrafficLogger.Save(); 
            HardwareHistoryLogger.Save();
            src.WebServer.LiteWebServer.Instance?.Stop();
            
            base.OnFormClosed(e);
            
            _ui?.Dispose();
            _bizHelper.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _displayRefreshTimer.Dispose();
        }
    }
}
