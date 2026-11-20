using System.Diagnostics;
using WindowRestorer.Core;

namespace WindowRestorer.App;

/// <summary>The tray icon and its menu; owns the layout service and the automatic triggers.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly DataPaths _paths;
    private readonly string[] _passThroughArgs;
    private AppSettings _settings;
    private readonly LayoutService _layouts;
    private readonly SystemEventsWindow _events;
    private readonly TriggerController _triggers;
    private readonly NotifyIcon _tray;
    private SettingsForm? _settingsForm;
    private PreviewForm? _previewForm;

    public TrayApp(DataPaths paths, string instanceId, AppSettings settings, bool logon, bool adminDeclined, string[] passThroughArgs)
    {
        _paths = paths;
        _settings = settings;
        _passThroughArgs = passThroughArgs;
        _layouts = new LayoutService(paths, () => _settings);
        _events = new SystemEventsWindow(instanceId);
        _events.ShowSettingsRequested += ShowSettings;
        _triggers = new TriggerController(_layouts, () => _settings, _events);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Remember now", null, (_, _) => RememberNow());
        menu.Items.Add("Restore now", null, async (_, _) => await RestoreNowAsync());
        menu.Items.Add("Preview restore…", null, (_, _) => ShowPreview());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Open data folder", null, (_, _) => Process.Start("explorer.exe", _paths.Folder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { Icon = AppIcon.Load(), ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _triggers.StatusChanged += UpdateTooltip;
        UpdateTooltip();

        if (logon && _settings.RestoreAtLogon) _triggers.StartLogonPlacement();
        else _triggers.SaveAtStart();
        if (adminDeclined)
        {
            Notify("Running without admin rights", "Windows of programs running as administrator can't be moved.", ToolTipIcon.Warning);
        }
    }

    private void RememberNow()
    {
        var result = _triggers.RememberNow();
        Notify("Layout remembered", $"{result.Saved} window(s) saved{(result.KeptNotOpen > 0 ? $", {result.KeptNotOpen} closed window(s) kept" : "")}.");
    }

    private async Task RestoreNowAsync()
    {
        if (_layouts.PickProfile(DisplaySet.Capture()).Profile == null)
        {
            Notify("Nothing to restore", "No layout saved for these monitors yet. Use Remember now first.");
            return;
        }
        var result = await _triggers.RestoreNowAsync("manual");
        Notify("Layout restored",
            $"{result.Moved} moved, {result.AlreadyInPlace} already in place" +
            (result.Failed > 0 ? $", {result.Failed} failed (see log)" : "") +
            (result.NotOpen > 0 ? $", {result.NotOpen} not open" : "") + ".",
            result.Failed > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void ShowPreview()
    {
        _previewForm?.Close();
        _previewForm = new PreviewForm(_layouts.PlanRestore());
        _previewForm.RestoreRequested += async () => await RestoreNowAsync();
        _previewForm.Show();
        _previewForm.Activate();
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings, _layouts);
        _settingsForm.SettingsChanged += SaveSettings;
        _settingsForm.RememberRequested += RememberNow;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void SaveSettings(AppSettings updated)
    {
        var old = _settings;
        _settings = updated;
        JsonFile.Save(_paths.Settings, _settings);
        Log.Info("Settings saved");
        _triggers.ApplySettings();

        if (old.StartWithWindows != updated.StartWithWindows || (updated.StartWithWindows && old.RunAsAdmin != updated.RunAsAdmin))
        {
            if (!StartupTask.Apply(updated.StartWithWindows, updated.RunAsAdmin, _passThroughArgs))
            {
                _settings.StartWithWindows = old.StartWithWindows;
                JsonFile.Save(_paths.Settings, _settings);
                _settingsForm?.Display(_settings);
                Notify("Start with Windows not changed", "Couldn't update the login task (see log).", ToolTipIcon.Warning);
            }
        }
        if (old.RunAsAdmin != updated.RunAsAdmin)
        {
            Notify("Restart needed", "The admin setting applies the next time Window Restorer starts.");
        }
    }

    private void UpdateTooltip()
    {
        var text = $"Window Restorer – {_triggers.Status}";
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info) => _tray.ShowBalloonTip(4000, title, text, icon);

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _triggers.Dispose();
        _events.Dispose();
        _settingsForm?.Close();
        _previewForm?.Close();
        base.ExitThreadCore();
    }
}

public static class AppIcon
{
    public static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("WindowRestorer.icon.ico")
                           ?? throw new InvalidOperationException("Embedded icon missing");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
