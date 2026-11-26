using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService; // [New]

namespace LiteMonitor.src.UI.Controls
{
    public static class MonitorLayout
    {
        public static readonly int H_ROW = UIUtils.S(44);
        
        public static readonly int X_COL1 = UIUtils.S(20);  
        public static readonly int X_COL2 = UIUtils.S(140);
        
        // ★★★ [新增] 单位列 (位于 Name Input 和 Checkbox 之间) ★★★
        public static readonly int X_COL_UNIT = UIUtils.S(300); 

        // [需求4] 原有列全部右移
        public static readonly int X_COL3 = UIUtils.S(390); 
        public static readonly int X_COL4 = UIUtils.S(510); 

        // 兼容
        public static readonly int X_ID = X_COL1;
        public static readonly int X_NAME = X_COL2;
        public static readonly int X_SWITCH = X_COL3;
        public static readonly int X_SORT = X_COL4;
    }

    public class MonitorItemRow : Panel
    {
        public MonitorItemConfig Config { get; }

        private readonly MonitorTextField _name;
        private readonly MonitorTextField _unit;
        private readonly LiteCheck _visible;
        private readonly bool _taskbar;

        public event EventHandler? MoveUp;
        public event EventHandler? MoveDown;

        public MonitorItemRow(MonitorItemConfig item, bool isTaskbarMode)
        {
            Config = item;
            _taskbar = isTaskbarMode;
            Dock = DockStyle.Top;
            Height = MonitorLayout.H_ROW;
            BackColor = Color.White;

            string resolved = isTaskbarMode ? MetricLabelResolver.ResolveLabel(item) : item.Key;
            var label = new Label
            {
                Text = string.IsNullOrWhiteSpace(resolved) ? DefaultName(item, shortName: false) : resolved,
                Location = new Point(MonitorLayout.X_COL1, UIUtils.S(14)),
                Size = new Size(UIUtils.S(110), UIUtils.S(20)),
                AutoEllipsis = true,
                ForeColor = UIColors.TextSub,
                Font = UIFonts.Regular(8F)
            };

            var unitContext = isTaskbarMode ? MetricUtils.UnitContext.SettingsTaskbar : MetricUtils.UnitContext.SettingsPanel;
            _name = new MonitorTextField(isTaskbarMode ? item.TaskbarLabel : item.UserLabel, DefaultName(item, isTaskbarMode),
                hideValue: " ", defaultValue: "", width: 148)
            {
                Location = new Point(MonitorLayout.X_COL2, UIUtils.S(8))
            };
            _unit = new MonitorTextField(isTaskbarMode ? item.UnitTaskbar : item.UnitPanel,
                MetricUtils.GetUnitStr(item.Key, 0, unitContext), hideValue: "", defaultValue: null, width: 82)
            {
                Location = new Point(MonitorLayout.X_COL_UNIT, UIUtils.S(8))
            };

            _visible = new LiteCheck(isTaskbarMode ? item.VisibleInTaskbar : item.VisibleInPanel,
                LanguageManager.T(isTaskbarMode ? "Menu.Taskbar" : "Menu.MainForm"))
            {
                Location = new Point(MonitorLayout.X_COL3, UIUtils.S(10))
            };
            var up = new LiteSortBtn("▲") { Location = new Point(MonitorLayout.X_COL4, UIUtils.S(10)) };
            var down = new LiteSortBtn("▼") { Location = new Point(MonitorLayout.X_COL4 + UIUtils.S(36), UIUtils.S(10)) };
            up.Click += (_, _) => MoveUp?.Invoke(this, EventArgs.Empty);
            down.Click += (_, _) => MoveDown?.Invoke(this, EventArgs.Empty);
            Controls.AddRange([label, _name, _unit, _visible, up, down]);
        }

        private static string DefaultName(MonitorItemConfig item, bool shortName)
        {
            string property = shortName ? "PROP.ShortLabel." : "PROP.Label.";
            string dynamicName = InfoService.Instance.GetValue(property + item.Key);
            if (!string.IsNullOrWhiteSpace(dynamicName)) return dynamicName;
            string? cached = shortName ? item.DynamicTaskbarLabel : item.DynamicLabel;
            if (!string.IsNullOrWhiteSpace(cached)) return cached;
            string key = (shortName ? "Short." : "Items.") + item.Key;
            string original = LanguageManager.GetOriginal(key);
            return original == item.Key ? item.Key[(item.Key.LastIndexOf('.') + 1)..] : original;
        }

        public void SetVisible(bool check) => _visible.Checked = check;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(UIColors.Border);
            e.Graphics.DrawLine(pen, MonitorLayout.X_COL1, Height - 1, Width - UIUtils.S(20), Height - 1);
        }

        public void SyncToConfig()
        {
            if (_taskbar)
            {
                Config.TaskbarLabel = _name.Value!;
                Config.UnitTaskbar = _unit.Value;
                Config.VisibleInTaskbar = _visible.Checked;
            }
            else
            {
                Config.UserLabel = _name.Value!;
                Config.UnitPanel = _unit.Value;
                Config.VisibleInPanel = _visible.Checked;
            }
        }

        private sealed class MonitorTextField : Panel
        {
            private enum Mode { Default, Custom, Hidden }

            private readonly LiteUnderlineInput _input;
            private readonly ContextMenuStrip _choices = new();
            private readonly string _defaultText;
            private readonly string? _defaultValue;
            private readonly string _hideValue;
            private string _custom = "";
            private Mode _mode;

            public MonitorTextField(string? stored, string defaultText, string hideValue, string? defaultValue, int width)
            {
                _defaultText = defaultText;
                _defaultValue = defaultValue;
                _hideValue = hideValue;
                Size = UIUtils.S(new Size(width, 27));

                var menu = new LiteSortBtn("⌄") { Dock = DockStyle.Right };
                _input = new LiteUnderlineInput("", fontColor: UIColors.TextMain) { Dock = DockStyle.Fill };
                Controls.Add(_input);
                Controls.Add(menu);
                menu.Click += (_, _) => _choices.Show(menu, 0, menu.Height);
                _input.Inner.ContextMenuStrip = _choices;
                AccessibleName = menu.AccessibleName = LanguageManager.T("Menu.FieldDisplayMode");

                AddChoice("Menu.FieldDefault", Mode.Default);
                AddChoice("Menu.FieldCustom", Mode.Custom);
                AddChoice("Menu.FieldHidden", Mode.Hidden);

                bool noValue = stored == null || stored == defaultValue;
                bool hidden = !noValue && string.IsNullOrWhiteSpace(stored);
                if (!noValue && !hidden) _custom = stored!;
                Select(noValue ? Mode.Default : hidden ? Mode.Hidden : Mode.Custom, focus: false);
            }

            public string? Value => _mode switch
            {
                Mode.Hidden => _hideValue,
                Mode.Custom when !string.IsNullOrWhiteSpace(_input.Inner.Text) => _input.Inner.Text.Trim(),
                _ => _defaultValue
            };

            private void AddChoice(string key, Mode mode)
            {
                var item = new ToolStripMenuItem(LanguageManager.T(key)) { Tag = mode };
                item.Click += (_, _) => Select(mode, focus: true);
                _choices.Items.Add(item);
            }

            private void Select(Mode mode, bool focus)
            {
                if (_mode == Mode.Custom) _custom = _input.Inner.Text;
                _mode = mode;
                foreach (ToolStripMenuItem item in _choices.Items)
                    item.Checked = (Mode)item.Tag! == mode;

                _input.SetTextColor(mode == Mode.Custom ? UIColors.TextMain : UIColors.TextSub);
                _input.Inner.ReadOnly = mode != Mode.Custom;
                _input.Inner.TabStop = mode == Mode.Custom;
                _input.Inner.Text = mode switch
                {
                    Mode.Custom => string.IsNullOrEmpty(_custom) ? _defaultText : _custom,
                    Mode.Hidden => LanguageManager.T("Menu.FieldHidden"),
                    _ => _defaultText.Length == 0 ? LanguageManager.T("Menu.FieldDefault") : _defaultText
                };
                if (focus && mode == Mode.Custom)
                {
                    _input.Inner.Focus();
                    _input.Inner.SelectAll();
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _choices.Dispose();
                base.Dispose(disposing);
            }
        }
    }

    public class MonitorGroupHeader : Panel
    {
        public string GroupKey { get; private set; }
        public LiteUnderlineInput InputAlias { get; private set; }
        private LiteCheck _chkAll; // [需求2] 增加组开关
        public event EventHandler MoveUp;
        public event EventHandler MoveDown;
        public event EventHandler<bool> ToggleGroup; // [需求2] 组开关事件

        public MonitorGroupHeader(string groupKey, string alias)
        {
            this.GroupKey = groupKey;
            this.Dock = DockStyle.Top;
            this.Height = UIUtils.S(45);
            this.BackColor = UIColors.GroupHeader; 

            var lblId = new Label { 
                Text = groupKey, 
                Location = new Point(MonitorLayout.X_COL1, UIUtils.S(12)), 
                AutoSize = true, 
                Font = UIFonts.Bold(9F), 
                ForeColor = Color.Gray 
            };

            string defGName = LanguageManager.GetOriginal("Groups." + groupKey);
            if (defGName.StartsWith("Groups.")) defGName = groupKey;
            
            InputAlias = new LiteUnderlineInput(string.IsNullOrEmpty(alias) ? defGName : alias, "", "", 100) 
            { Location = new Point(MonitorLayout.X_COL2, UIUtils.S(8)) };
            InputAlias.SetBg(UIColors.GroupHeader); 
            InputAlias.Inner.Font = UIFonts.Bold(9F);

            // [需求2] 组开关复选框
            _chkAll = new LiteCheck(true, "") 
            { 
                Location = new Point(MonitorLayout.X_COL3, UIUtils.S(12)),
                // 默认不显示文字，只作为全选/全不选开关
            };
            // 点击事件：触发 ToggleGroup
            _chkAll.Click += (s, e) => ToggleGroup?.Invoke(this, _chkAll.Checked);

            var btnUp = new LiteSortBtn("▲") { Location = new Point(MonitorLayout.X_COL4, UIUtils.S(10)) };
            var btnDown = new LiteSortBtn("▼") { Location = new Point(MonitorLayout.X_COL4 + UIUtils.S(36), UIUtils.S(10)) };
            
            btnUp.Click += (s, e) => MoveUp?.Invoke(this, EventArgs.Empty);
            btnDown.Click += (s, e) => MoveDown?.Invoke(this, EventArgs.Empty);

            this.Controls.AddRange(new Control[] { lblId, InputAlias, _chkAll, btnUp, btnDown });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using(var p = new Pen(UIColors.Border)) 
                e.Graphics.DrawLine(p, 0, Height-1, Width, Height-1);
        }
    }
}
