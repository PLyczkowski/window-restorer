using WindowRestorer.Core;

namespace WindowRestorer.App;

/// <summary>
/// Settings tab (triggers, fallback, startup; applied as soon as they change) and
/// Saved windows tab (browse / forget entries, remember now).
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly LayoutService _layouts;
    private readonly CheckBox _saveOnShutdown = Check("Save the layout before shutdown, restart or sign-out");
    private readonly CheckBox _autoSave = Check("Auto-save when a window is moved, and every");
    private readonly NumericUpDown _autoSaveSeconds = Number(10, 3600);
    private readonly CheckBox _restoreAtLogon = Check("At login, placing windows as they open, for");
    private readonly NumericUpDown _logonMinutes = Number(1, 30);
    private readonly CheckBox _restoreOnDisplayChange = Check("When monitors are connected, disconnected or rearranged");
    private readonly CheckBox _restoreOnWake = Check("After waking from sleep and when the screens turn back on");
    private readonly RadioButton _moveToNearest = Radio("Move the window to the nearest connected monitor");
    private readonly RadioButton _leave = Radio("Leave the window where Windows put it");
    private readonly CheckBox _startWithWindows = Check("Start with Windows");
    private readonly CheckBox _runAsAdmin = Check("Run as administrator (needed to move windows of programs running as admin)");
    private readonly ListView _entries = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, ShowGroups = true,
    };
    private readonly Label _entriesHint = Label("");
    private bool _displaying;

    /// <summary>Raised on every change, with the full new settings.</summary>
    public event Action<AppSettings>? SettingsChanged;
    public event Action? RememberRequested;

    public SettingsForm(AppSettings settings, LayoutService layouts)
    {
        _layouts = layouts;
        Text = "Window Restorer";
        Icon = AppIcon.Load();
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 540);
        MinimumSize = new Size(560, 420);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildSettingsTab());
        tabs.TabPages.Add(BuildEntriesTab());

        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(new Label { Text = "Changes apply right away.", AutoSize = true, Margin = new Padding(0, 8, 12, 0), ForeColor = SystemColors.GrayText });
        CancelButton = close;

        Controls.Add(tabs);
        Controls.Add(buttons);

        Display(settings);
        FillEntries();
        _layouts.Changed += OnLayoutsChanged;
        FormClosed += (_, _) => _layouts.Changed -= OnLayoutsChanged;
    }

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("Settings") { Padding = new Padding(10), AutoScroll = true };
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        stack.Controls.Add(Group("Remember", _saveOnShutdown, Row(_autoSave, _autoSaveSeconds, Label("seconds"))));
        stack.Controls.Add(Group("Restore", Row(_restoreAtLogon, _logonMinutes, Label("minutes")), _restoreOnDisplayChange, _restoreOnWake));
        stack.Controls.Add(Group("If a window's saved monitor isn't connected", _moveToNearest, _leave));
        stack.Controls.Add(Group("Startup", _startWithWindows, _runAsAdmin));
        page.Controls.Add(stack);
        _autoSave.CheckedChanged += (_, _) => _autoSaveSeconds.Enabled = _autoSave.Checked;
        _restoreAtLogon.CheckedChanged += (_, _) => _logonMinutes.Enabled = _restoreAtLogon.Checked;
        foreach (var box in new[] { _saveOnShutdown, _autoSave, _restoreAtLogon, _restoreOnDisplayChange, _restoreOnWake, _startWithWindows, _runAsAdmin })
        {
            box.CheckedChanged += (_, _) => OnChanged();
        }
        _moveToNearest.CheckedChanged += (_, _) => { if (_moveToNearest.Checked) OnChanged(); };
        _leave.CheckedChanged += (_, _) => { if (_leave.Checked) OnChanged(); };
        _autoSaveSeconds.ValueChanged += (_, _) => OnChanged();
        _logonMinutes.ValueChanged += (_, _) => OnChanged();
        return page;
    }

    private TabPage BuildEntriesTab()
    {
        var page = new TabPage("Saved windows") { Padding = new Padding(10) };
        _entries.Columns.Add("Program", 130);
        _entries.Columns.Add("Title", 250);
        _entries.Columns.Add("State", 80);
        _entries.Columns.Add("Position", 130);
        _entries.Columns.Add("Last seen", 120);
        var forget = new Button { Text = "Forget selected", AutoSize = true };
        forget.Click += (_, _) =>
        {
            var ids = _entries.SelectedItems.Cast<ListViewItem>().Select(i => (Guid)i.Tag!).ToList();
            if (ids.Count > 0) _layouts.Forget(ids);
        };
        var remember = new Button { Text = "Remember now", AutoSize = true };
        remember.Click += (_, _) => RememberRequested?.Invoke();
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        bottom.Controls.AddRange([remember, forget, _entriesHint]);
        _entriesHint.Margin = new Padding(8, 8, 0, 0);
        page.Controls.Add(_entries);
        page.Controls.Add(bottom);
        return page;
    }

    private void OnLayoutsChanged()
    {
        if (InvokeRequired) BeginInvoke(FillEntries);
        else FillEntries();
    }

    private void FillEntries()
    {
        _entries.BeginUpdate();
        _entries.Items.Clear();
        _entries.Groups.Clear();
        var current = DisplaySet.Capture().ProfileKey;
        foreach (var profile in _layouts.Data.Profiles.OrderByDescending(p => p.Key == current).ThenByDescending(p => p.LastSaved))
        {
            var group = new ListViewGroup($"{LayoutService.Describe(profile)}{(profile.Key == current ? "  (connected now)" : "")}");
            _entries.Groups.Add(group);
            foreach (var e in profile.Windows.OrderBy(e => Path.GetFileName(e.ExePath), StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Title))
            {
                _entries.Items.Add(new ListViewItem([Path.GetFileName(e.ExePath), e.Title, e.State.ToString(), e.Rect.ToString(), e.LastSeen.ToString("g")], group) { Tag = e.Id });
            }
        }
        _entriesHint.Text = _entries.Items.Count == 0
            ? "Nothing saved yet. Click Remember now, or move a window and auto-save will pick it up."
            : "Closed windows stay in this list so their spot is reused when they open again.";
        _entries.EndUpdate();
    }

    private void OnChanged()
    {
        if (!_displaying) SettingsChanged?.Invoke(Collect());
    }

    /// <summary>Shows these settings without raising <see cref="SettingsChanged"/> (e.g. after a change was undone).</summary>
    public void Display(AppSettings s)
    {
        _displaying = true;
        try
        {
            DisplayCore(s);
        }
        finally
        {
            _displaying = false;
        }
    }

    private void DisplayCore(AppSettings s)
    {
        _saveOnShutdown.Checked = s.SaveOnShutdown;
        _autoSave.Checked = s.AutoSave;
        _autoSaveSeconds.Value = Math.Clamp(s.AutoSaveIntervalSeconds, 10, 3600);
        _autoSaveSeconds.Enabled = s.AutoSave;
        _restoreAtLogon.Checked = s.RestoreAtLogon;
        _logonMinutes.Value = Math.Clamp(s.LogonWatchMinutes, 1, 30);
        _logonMinutes.Enabled = s.RestoreAtLogon;
        _restoreOnDisplayChange.Checked = s.RestoreOnDisplayChange;
        _restoreOnWake.Checked = s.RestoreOnWake;
        _moveToNearest.Checked = s.MissingMonitor == MissingMonitorPolicy.MoveToNearest;
        _leave.Checked = s.MissingMonitor == MissingMonitorPolicy.Leave;
        _startWithWindows.Checked = s.StartWithWindows;
        _runAsAdmin.Checked = s.RunAsAdmin;
    }

    private AppSettings Collect() => new()
    {
        SaveOnShutdown = _saveOnShutdown.Checked,
        AutoSave = _autoSave.Checked,
        AutoSaveIntervalSeconds = (int)_autoSaveSeconds.Value,
        RestoreAtLogon = _restoreAtLogon.Checked,
        LogonWatchMinutes = (int)_logonMinutes.Value,
        RestoreOnDisplayChange = _restoreOnDisplayChange.Checked,
        RestoreOnWake = _restoreOnWake.Checked,
        MissingMonitor = _leave.Checked ? MissingMonitorPolicy.Leave : MissingMonitorPolicy.MoveToNearest,
        StartWithWindows = _startWithWindows.Checked,
        RunAsAdmin = _runAsAdmin.Checked,
    };

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };

    private static RadioButton Radio(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };

    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, Width = 64, Margin = new Padding(0, 2, 4, 0) };

    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 5, 3, 0) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        controls[0].Margin = new Padding(3, 4, 0, 4);
        return row;
    }

    private static GroupBox Group(string title, params Control[] controls)
    {
        var inner = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(4) };
        inner.Controls.AddRange(controls);
        var box = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(700, 0), Margin = new Padding(0, 0, 0, 10), Padding = new Padding(8) };
        box.Controls.Add(inner);
        return box;
    }
}
