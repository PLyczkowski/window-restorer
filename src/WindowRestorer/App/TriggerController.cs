using WindowRestorer.Core;
using WindowRestorer.Native;
using Timer = System.Windows.Forms.Timer;

namespace WindowRestorer.App;

/// <summary>
/// Decides when to remember and restore automatically:
/// - remember before shutdown, after a window is moved, and every minute (auto-save);
/// - restore after login (placing windows as they open), after monitors change, and after wake / screens on.
/// Auto-save pauses while monitors are changing, the PC sleeps or the screens are off, so a layout
/// that Windows scrambled is never saved over the good one.
/// </summary>
public sealed class TriggerController : IDisposable
{
    private static readonly TimeSpan SettleQuietTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SettleMaxWait = TimeSpan.FromSeconds(30);
    /// <summary>Windows sometimes moves windows again a few seconds after monitors come back; restore is re-checked after this.</summary>
    private static readonly TimeSpan SettleRecheckDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LogonWindowMinAge = TimeSpan.FromSeconds(1.5);

    private readonly LayoutService _layouts;
    private readonly Func<AppSettings> _settings;
    private readonly SystemEventsWindow _events;
    private readonly HashSet<string> _pauses = [];

    private readonly Timer _autoSaveTimer = new();
    private readonly Timer _moveDebounce = new() { Interval = 1500 };
    private readonly NativeMethods.WinEventProc _winEventProc;
    private IntPtr _winEventHook;

    private readonly Timer _settleTimer = new() { Interval = 500 };
    private string _stableSignature;
    private string _settleSignature = "";
    private DateTime _settleStarted, _settleLastChange;
    private bool _settleRestore;
    /// <summary>Bumped by every new settle, so an older one that's still finishing knows it was replaced.</summary>
    private int _settleGeneration;
    private readonly List<string> _settleReasons = [];

    private bool _suspended;
    private bool _screensOn = true;
    private bool _restoring;

    private readonly Timer _logonTimer = new() { Interval = 2000 };
    private DateTime _logonUntil;
    private readonly Dictionary<IntPtr, DateTime> _logonFirstSeen = [];
    private readonly HashSet<IntPtr> _logonHandled = [];
    private readonly HashSet<Guid> _logonUsedEntries = [];

    public TriggerController(LayoutService layouts, Func<AppSettings> settings, SystemEventsWindow events)
    {
        _layouts = layouts;
        _events = events;
        _settings = settings;
        _stableSignature = DisplaySet.Capture().Signature;

        _events.ShuttingDown += () => Guard("shutdown save", OnShuttingDown);
        _events.DisplayChanged += () => Guard("display change", () => BeginSettle("monitors changed", wake: false));
        _events.Suspending += () => Guard("sleep", () => { _suspended = true; Pause("asleep"); });
        _events.Resumed += () => Guard("wake", () => { _suspended = false; Resume("asleep"); BeginSettle("woke from sleep", wake: true); });
        _events.ScreensPowerChanged += on => Guard("screen power", () => OnScreensPower(on));

        _autoSaveTimer.Tick += (_, _) => Guard("auto-save", () => AutoSave("every minute"));
        _moveDebounce.Tick += (_, _) => Guard("auto-save", () => { _moveDebounce.Stop(); AutoSave("window moved"); });
        _settleTimer.Tick += (_, _) => GuardAsync("display settle", SettleTick);
        _logonTimer.Tick += (_, _) => GuardAsync("login placement", LogonTick);

        _winEventProc = OnWinEvent;
        _winEventHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_MOVESIZEEND, NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _winEventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        if (_winEventHook == IntPtr.Zero) Log.Error("Couldn't watch window moves; auto-save will only run every minute");

        ApplySettings();
    }

    /// <summary>Short status for the tray tooltip.</summary>
    public string Status =>
        _restoring ? "restoring…" :
        _pauses.Count > 0 ? $"auto-save paused ({string.Join(", ", _pauses)})" :
        _settings().AutoSave ? "auto-save on" : "auto-save off";

    public event Action? StatusChanged;

    /// <summary>Call after settings change.</summary>
    public void ApplySettings()
    {
        _autoSaveTimer.Interval = Math.Max(10, _settings().AutoSaveIntervalSeconds) * 1000;
        _autoSaveTimer.Enabled = _settings().AutoSave;
        StatusChanged?.Invoke();
    }

    /// <summary>After login: for a few minutes, put each window back as soon as it opens.</summary>
    public void StartLogonPlacement()
    {
        _logonUntil = DateTime.Now.AddMinutes(Math.Max(1, _settings().LogonWatchMinutes));
        Pause("placing windows after login");
        Log.Info($"Placing windows as they open until {_logonUntil:HH:mm:ss}");
        _logonTimer.Start();
        GuardAsync("login placement", LogonTick);
    }

    public SaveResult RememberNow() => _layouts.Remember("manual");

    /// <summary>When started by hand, save right away (if auto-save is on) instead of waiting for the first interval.</summary>
    public void SaveAtStart() => Guard("save at start", () => AutoSave("app started"));

    public async Task<RestoreResult> RestoreNowAsync(string reason, RestorePlan? plan = null)
    {
        if (_restoring)
        {
            Log.Info($"Restore ({reason}) skipped: another restore is running");
            return RestoreResult.Nothing;
        }
        _restoring = true;
        StatusChanged?.Invoke();
        try
        {
            return await _layouts.RestoreAsync(reason, plan);
        }
        finally
        {
            _restoring = false;
            StatusChanged?.Invoke();
        }
    }

    private void OnShuttingDown()
    {
        if (!_settings().SaveOnShutdown) return;
        if (!CanSaveNow())
        {
            Log.Info("Shutdown: not saving because the layout isn't settled (monitors changing, screens off or login placement); keeping the last good save");
            return;
        }
        _layouts.Remember("shutdown");
    }

    private void OnScreensPower(bool on)
    {
        if (!on)
        {
            if (!_screensOn) return;
            _screensOn = false;
            Pause("screens off");
            Log.Info("Screens turned off");
            return;
        }
        if (_screensOn) return; // Also the initial "on" report right after subscribing.
        _screensOn = true;
        Resume("screens off");
        BeginSettle("screens turned on", wake: true);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != NativeMethods.OBJID_WINDOW) return;
        if (eventType is not (NativeMethods.EVENT_SYSTEM_MOVESIZEEND or NativeMethods.EVENT_SYSTEM_MINIMIZESTART or NativeMethods.EVENT_SYSTEM_MINIMIZEEND)) return;
        if (!_settings().AutoSave) return;
        _moveDebounce.Stop();
        _moveDebounce.Start();
    }

    private void AutoSave(string reason)
    {
        if (!_settings().AutoSave || !CanSaveNow()) return;
        _layouts.Remember($"auto-save, {reason}");
    }

    /// <summary>Safe to save only when nothing is paused and the monitors are still the ones we last saw settle.</summary>
    private bool CanSaveNow()
    {
        if (_pauses.Count > 0 || _restoring) return false;
        var signature = DisplaySet.Capture().Signature;
        if (signature == _stableSignature) return true;
        // Monitors changed but the change notice hasn't arrived (or was missed): treat it as one now.
        BeginSettle("monitors changed", wake: false);
        return false;
    }

    /// <summary>
    /// Pauses auto-save until the monitor setup has stopped changing, then restores. The restore runs even
    /// if the same monitors are back as before: windows were moved while one was missing (e.g. a monitor
    /// switched off and on again), and a restore with nothing to fix moves nothing.
    /// </summary>
    private void BeginSettle(string reason, bool wake)
    {
        _settleRestore |= wake ? _settings().RestoreOnWake : _settings().RestoreOnDisplayChange;
        if (!_settleReasons.Contains(reason)) _settleReasons.Add(reason);
        if (_settleTimer.Enabled) return;

        _settleGeneration++;
        _settleStarted = _settleLastChange = DateTime.Now;
        _settleSignature = TryGetSignature();
        Log.Info($"Waiting for monitors to settle ({reason}); monitors now: {DescribeMonitors()}");
        Pause("monitors settling");
        _settleTimer.Start();
    }

    private async Task SettleTick()
    {
        var now = DateTime.Now;
        if (_suspended || !_screensOn)
        {
            // Nothing to settle while asleep/dark; the clock starts again when they're back.
            _settleStarted = _settleLastChange = now;
            return;
        }
        var signature = TryGetSignature();
        if (signature != _settleSignature)
        {
            _settleSignature = signature;
            _settleLastChange = now;
            Log.Info($"Monitors now: {DescribeMonitors()}");
        }
        if (signature.Length == 0)
        {
            // No monitors at all for a moment (all switched off/unplugged): keep waiting.
            _settleStarted = now;
            return;
        }
        if (now - _settleLastChange < SettleQuietTime && now - _settleStarted < SettleMaxWait) return;

        _settleTimer.Stop();
        var generation = _settleGeneration;
        _stableSignature = signature;
        var restore = _settleRestore;
        var reasons = string.Join(", ", _settleReasons);
        _settleRestore = false;
        _settleReasons.Clear();
        Log.Info($"Monitors settled ({reasons}){(restore ? ", restoring" : "")}");
        try
        {
            if (!restore) return;
            await RestoreNowAsync(reasons);
            if (generation != _settleGeneration) return;
            await Task.Delay(SettleRecheckDelay);
            if (generation != _settleGeneration) return;
            if (TryGetSignature() != _stableSignature)
            {
                BeginSettle("monitors changed", wake: false);
                return;
            }
            await RestoreNowAsync($"{reasons}, re-check");
        }
        finally
        {
            // Only the newest settle may resume auto-save; a newer one owns the pause now.
            if (generation == _settleGeneration && !_settleTimer.Enabled) Resume("monitors settling");
        }
    }

    private async Task LogonTick()
    {
        if (DateTime.Now > _logonUntil)
        {
            _logonTimer.Stop();
            Resume("placing windows after login");
            Log.Info("Finished placing windows after login");
            return;
        }
        if (_restoring || _pauses.Contains("monitors settling") || _suspended || !_screensOn) return;

        var displays = DisplaySet.Capture();
        var now = DateTime.Now;
        var ready = new HashSet<IntPtr>();
        foreach (var w in _layouts.CurrentWindows(displays))
        {
            if (_logonHandled.Contains(w.Hwnd)) continue;
            // Give each new window a moment: many programs position themselves right after opening.
            if (!_logonFirstSeen.TryGetValue(w.Hwnd, out var firstSeen)) _logonFirstSeen[w.Hwnd] = now;
            else if (now - firstSeen >= LogonWindowMinAge) ready.Add(w.Hwnd);
        }
        if (ready.Count == 0) return;

        var plan = _layouts.PlanRestore(w => ready.Contains(w.Hwnd), _logonUsedEntries);
        _logonHandled.UnionWith(ready);
        foreach (var m in plan.Moves) _logonUsedEntries.Add(m.Entry.Id);
        if (plan.Moves.Count > 0) await RestoreNowAsync("login", plan);
    }

    private void Pause(string reason)
    {
        if (_pauses.Add(reason)) StatusChanged?.Invoke();
    }

    private void Resume(string reason)
    {
        if (_pauses.Remove(reason)) StatusChanged?.Invoke();
    }

    private static string DescribeMonitors()
    {
        try
        {
            var displays = DisplaySet.Capture();
            return $"{displays.Displays.Count} ({string.Join(", ", displays.Displays.Select(d => $"{d.Name} {d.Bounds}"))})";
        }
        catch (InvalidOperationException)
        {
            return "none";
        }
    }

    private static string TryGetSignature()
    {
        try
        {
            return DisplaySet.Capture().Signature;
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"{what} failed", ex);
        }
    }

    private static async void GuardAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error($"{what} failed", ex);
        }
    }

    public void Dispose()
    {
        if (_winEventHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_winEventHook);
        _winEventHook = IntPtr.Zero;
        foreach (var t in new[] { _autoSaveTimer, _moveDebounce, _settleTimer, _logonTimer }) t.Dispose();
    }
}
