using System;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer;
using LiteMonitor.src.Core.Actions;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class MainPanelPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;

        public MainPanelPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) };
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null) return;
            if (_isLoaded) { RefreshWebStatus(); return; }

            _container.SuspendLayout();
            ClearAndDispose(_container.Controls);

            CreateBehaviorCard();
            CreateAppearanceCard();
            CreateHorizontalModeGroup();
            CreateWebCard();

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private void CreateBehaviorCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.MainFormSettings"));

            // 1. Hide Main Form (with logic)
            var chkHide = group.AddToggle(this, "Menu.HideMainForm", 
                () => Config.HideMainForm, 
                v => Config.HideMainForm = v);
            
            chkHide.CheckedChanged += (s, e) => EnsureSafeVisibility(chkHide, null, null);

            // 2. Toggles
            group.AddToggle(this, "Menu.TopMost", () => Config.TopMost, v => Config.TopMost = v);
            group.AddToggle(this, "Menu.ClampToScreen", () => Config.ClampToScreen, v => Config.ClampToScreen = v);
            group.AddToggle(this, "Menu.AutoHide", () => Config.AutoHide, v => Config.AutoHide = v);
            group.AddToggle(this, "Menu.ClickThrough", () => Config.ClickThrough, v => Config.ClickThrough = v);

            // 3. Double Click Action
            string[] actions = { 
                LanguageManager.T("Menu.ActionSwitchLayout"),
                LanguageManager.T("Menu.ActionTaskMgr"),
                LanguageManager.T("Menu.ActionSettings"),
                LanguageManager.T("Menu.ActionTrafficHistory"),
                LanguageManager.T("Menu.CleanMemory"),
                LanguageManager.T("Menu.OpenWeb")
            };
            group.AddComboIndex(this, "Menu.DoubleClickAction", actions,
                () => Config.MainFormDoubleClickAction,
                idx => Config.MainFormDoubleClickAction = idx
            );

            AddGroupToPage(group);
        }

        private void CreateAppearanceCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.Appearance"));

            // 1. Theme
            group.AddCombo(this, "Menu.Theme", ThemeManager.GetAvailableThemes(), 
                () => Config.Skin, 
                v => Config.Skin = v);

            // 2. Orientation
            group.AddComboIndex(this, "Menu.DisplayMode", 
                new[] { LanguageManager.T("Menu.Vertical"), LanguageManager.T("Menu.Horizontal") },
                () => Config.HorizontalMode ? 1 : 0, 
                idx => Config.HorizontalMode = (idx == 1));

            // 3. Width
            int[] widths = { 180, 200, 220, 240, 260, 280, 300, 360, 420, 480, 540, 600, 660, 720, 780, 840, 900, 960, 1020, 1080, 1140, 1200 };
            group.AddCombo(this, "Menu.Width", 
                widths.Select(w => w + " px"), 
                () => Config.PanelWidth + " px",
                s => Config.PanelWidth = MetricUtils.ParseInt(s));

            // 4. Opacity
            double[] opacities = { 1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5, 0.4, 0.3 };
            group.AddCombo(this, "Menu.Opacity",
                opacities.Select(o => Math.Round(o * 100) + "%"),
                () => Math.Round(Config.Opacity * 100) + "%",
                s => Config.Opacity = MetricUtils.ParseDouble(s) / 100.0);

            // 5. Scale
            double[] scales = { 2.0, 1.75, 1.5, 1.25, 1.0, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5 };
            group.AddCombo(this, "Menu.Scale",
                scales.Select(s => (s * 100) + "%"),
                () => (Config.UIScale * 100) + "%",
                s => Config.UIScale = MetricUtils.ParseDouble(s) / 100.0);

            AddGroupToPage(group);

        }

        private void CreateHorizontalModeGroup()
        {
            // --- Group: Horizontal Mode ---
            var groupHorz = new LiteSettingsGroup(LanguageManager.T("Menu.Horizontal"));

          

            // 2. Single Line
            groupHorz.AddToggle(this, "Menu.TaskbarSingleLine",
                () => Config.HorizontalSingleLine,
                v => Config.HorizontalSingleLine = v);

            // 3. Spacing
            groupHorz.AddInt(this, "Menu.TaskbarItemSpacing", "px",
                () => Config.HorizontalItemSpacing,
                v => Config.HorizontalItemSpacing = v);

            groupHorz.AddInt(this, "Menu.TaskbarInnerSpacing", "px",
                () => Config.HorizontalInnerSpacing,
                v => Config.HorizontalInnerSpacing = v);
            
            AddGroupToPage(groupHorz);
        }

        private Label? _webStatus;
        private Button? _openWeb;
        private Button? _copyWeb;

        private void CreateWebCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.WebServer"));
            var enabled = group.AddToggle(this, "Menu.WebServer", () => Config.WebServerEnabled,
                value => Config.WebServerEnabled = value);
            var scope = group.AddComboIndex(this, "Menu.WebAccessScope",
                new[] { LanguageManager.T("Menu.WebThisPc"), LanguageManager.T("Menu.WebNetworkHttps") },
                () => Config.WebServerNetworkAccess ? 1 : 0, value => Config.WebServerNetworkAccess = value == 1);
            scope.Width = UIUtils.S(170);
            var port = group.AddInt(this, "Menu.WebServerPort", "", () => Config.WebServerPort,
                value => Config.WebServerPort = value);
            var certificate = group.AddInput(this, "Menu.WebCertificate", () => Config.WebServerCertificateThumbprint ?? "",
                value => Config.WebServerCertificateThumbprint = value, width: 230);
            var password = group.AddInput(this, "Menu.WebServerPassword", () => Config.WebServerPassword,
                value => Config.WebServerPassword = value, LanguageManager.T("Menu.WebPasswordHint"), width: 180);
            password.Inner.UseSystemPasswordChar = true;
            password.Inner.MaxLength = 256;

            void SynchronizeScope()
            {
                scope.Enabled = port.Enabled = enabled.Checked;
                certificate.Parent!.Visible = password.Parent!.Visible = enabled.Checked && Config.WebServerNetworkAccess;
            }

            enabled.CheckedChanged += (_, _) => SynchronizeScope();
            scope.Inner.SelectedIndexChanged += (_, _) => SynchronizeScope();
            RegisterRefresh(SynchronizeScope);

            _webStatus = new Label
            {
                Dock = DockStyle.Top,
                Padding = UIUtils.S(new Padding(12, 6, 12, 6)),
                AutoSize = true,
                MaximumSize = new Size(UIUtils.S(490), 0),
                ForeColor = UIColors.TextSub
            };
            group.AddFullItem(_webStatus);
            _openWeb = new LiteButton(LanguageManager.T("Menu.OpenWeb"), false);
            _copyWeb = new LiteButton(LanguageManager.T("Menu.WebCopyAddress"), false);
            _openWeb.Click += (_, _) => WebActions.OpenWebMonitor();
            _copyWeb.Click += (_, _) => WebActions.CopyWebAddress();
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            actions.Controls.AddRange([_openWeb, _copyWeb]);
            group.AddAction(LanguageManager.T("Menu.WebAddress"), actions);

            AddGroupToPage(group);
            SynchronizeScope();
            RefreshWebStatus();
        }

        internal void RefreshWebStatus()
        {
            if (_webStatus == null || _openWeb == null || _copyWeb == null) return;
            var server = LiteWebServer.Instance;
            _webStatus.Text = server?.LastError ?? server?.Address?.AbsoluteUri ?? LanguageManager.T("Menu.WebStopped");
            _openWeb.Enabled = _copyWeb.Enabled = server?.Address != null;
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
    }
}
