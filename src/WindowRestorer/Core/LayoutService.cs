namespace WindowRestorer.Core;

public sealed record SaveResult(string ProfileKey, int Updated, int Added, int KeptNotOpen)
{
    public int Saved => Updated + Added;
}

public sealed record RestorePlan(
    Profile? Profile,
    bool IsFallbackProfile,
    DisplaySet Displays,
    IReadOnlyList<PlannedMove> Moves,
    IReadOnlyList<WindowEntry> NotOpen,
    IReadOnlyList<LiveWindow> Unmatched)
{
    public IEnumerable<PlannedMove> ToMove => Moves.Where(m => m.Action == PlanAction.Move);
}

public sealed record RestoreResult(int Moved, int Failed, int AlreadyInPlace, int Skipped, int NotOpen)
{
    public static readonly RestoreResult Nothing = new(0, 0, 0, 0, 0);
}

/// <summary>Remember / plan / restore on top of the saved layout file. Use from one thread (the UI thread).</summary>
public sealed class LayoutService
{
    private readonly DataPaths _paths;
    private readonly Func<AppSettings> _settings;
    private readonly Func<LiveWindow, bool> _windowFilter;

    /// <param name="windowFilter">Which open windows to consider. The app uses all of them; tests limit it to their own windows.</param>
    public LayoutService(DataPaths paths, Func<AppSettings> settings, Func<LiveWindow, bool>? windowFilter = null)
    {
        _paths = paths;
        _settings = settings;
        _windowFilter = windowFilter ?? (_ => true);
        Data = JsonFile.Load<LayoutData>(paths.Layouts);
    }

    public LayoutData Data { get; }

    public event Action? Changed;

    public List<LiveWindow> CurrentWindows(DisplaySet displays) =>
        WindowEnumerator.Enumerate(displays).Where(_windowFilter).ToList();

    /// <summary>
    /// Saves the open windows into the profile for the connected monitors. Entries of windows that
    /// aren't open are kept as they are, so they can be reused when that window comes back.
    /// </summary>
    public SaveResult Remember(string reason)
    {
        var displays = DisplaySet.Capture();
        var windows = CurrentWindows(displays);
        var profile = Data.Profiles.FirstOrDefault(p => p.Key == displays.ProfileKey);
        if (profile == null)
        {
            profile = new Profile { Key = displays.ProfileKey };
            Data.Profiles.Add(profile);
        }

        var matches = WindowMatcher.Match(profile.Windows, windows, allowSimilar: false);
        var matchedEntries = matches.Select(m => m.Entry).ToHashSet();
        KeepUnmatchedEntriesConsistent(profile, displays, matchedEntries);
        profile.Monitors = displays.Displays.Select(d => d.ToSaved()).ToList();

        var now = DateTime.Now;
        foreach (var (window, entry, _) in matches) Fill(entry, window, now);
        var matchedWindows = matches.Select(m => m.Window).ToHashSet();
        var added = 0;
        foreach (var window in windows.Where(w => !matchedWindows.Contains(w)))
        {
            var entry = new WindowEntry { ExePath = window.ExePath, ClassName = window.ClassName, Title = window.Title, MonitorId = window.MonitorId };
            Fill(entry, window, now);
            profile.Windows.Add(entry);
            added++;
        }
        profile.LastSaved = now;
        Persist();

        var result = new SaveResult(profile.Key, matches.Count, added, profile.Windows.Count - matches.Count - added);
        Log.Info($"Remembered ({reason}): {result.Updated} updated, {result.Added} new, {result.KeptNotOpen} kept for closed windows; profile {Describe(profile)}");
        return result;
    }

    /// <summary>Works out what a restore would do, without moving anything.</summary>
    /// <param name="onlyWindows">Limit to these windows (used to place windows as they open after login).</param>
    /// <param name="excludeEntries">Entries already used in this login, so two windows don't take the same spot.</param>
    public RestorePlan PlanRestore(Func<LiveWindow, bool>? onlyWindows = null, IReadOnlySet<Guid>? excludeEntries = null)
    {
        var displays = DisplaySet.Capture();
        var windows = CurrentWindows(displays).Where(onlyWindows ?? (_ => true)).ToList();
        var (profile, fallback) = PickProfile(displays);
        if (profile == null) return new RestorePlan(null, false, displays, [], [], windows);

        var entries = profile.Windows.Where(e => excludeEntries == null || !excludeEntries.Contains(e.Id)).ToList();
        var matches = WindowMatcher.Match(entries, windows, allowSimilar: true);
        var moves = matches.Select(m => PlacementPlanner.Plan(m, profile, displays, _settings().MissingMonitor)).ToList();
        var matchedEntries = matches.Select(m => m.Entry).ToHashSet();
        var matchedWindows = matches.Select(m => m.Window).ToHashSet();
        return new RestorePlan(profile, fallback, displays, moves,
            entries.Where(e => !matchedEntries.Contains(e)).ToList(),
            windows.Where(w => !matchedWindows.Contains(w)).ToList());
    }

    /// <summary>Moves windows per the plan, checks the result and retries once (windows crossing to a monitor with different scaling often resize themselves).</summary>
    public async Task<RestoreResult> RestoreAsync(string reason, RestorePlan? plan = null)
    {
        plan ??= PlanRestore();
        if (plan.Profile == null)
        {
            Log.Info($"Restore ({reason}): nothing saved for these monitors yet");
            return RestoreResult.Nothing;
        }

        var toMove = plan.ToMove.ToList();
        var pending = toMove.Where(m => WindowMover.Apply(m, plan.Displays)).ToList();
        var refused = toMove.Count - pending.Count;
        if (pending.Count > 0)
        {
            await Task.Delay(400);
            pending = pending.Where(m => !WindowMover.IsInPlace(m, plan.Displays)).ToList();
        }
        if (pending.Count > 0)
        {
            foreach (var m in pending) WindowMover.Apply(m, plan.Displays);
            await Task.Delay(400);
            pending = pending.Where(m => !WindowMover.IsInPlace(m, plan.Displays)).ToList();
        }
        foreach (var m in pending)
        {
            var now = WindowEnumerator.Read(m.Window.Hwnd, plan.Displays);
            Log.Warn($"'{m.Window.Title}' didn't end up where planned: wanted {m.TargetState} {m.TargetRect}, got {now?.State} {now?.Rect}");
        }

        if (!plan.IsFallbackProfile)
        {
            // Tie entries to the windows they were matched with, so title changes later don't lose the link.
            foreach (var m in plan.Moves) Bind(m.Entry, m.Window);
            Persist();
        }

        var moved = toMove.Count - refused - pending.Count;
        var result = new RestoreResult(moved, refused + pending.Count,
            plan.Moves.Count(m => m.Action == PlanAction.AlreadyInPlace),
            plan.Moves.Count(m => m.Action is PlanAction.LeaveMissingMonitor or PlanAction.SkipHung),
            plan.NotOpen.Count);
        Log.Info($"Restored ({reason}) from profile {Describe(plan.Profile)}{(plan.IsFallbackProfile ? " (closest match for these monitors)" : "")}: " +
                 $"{result.Moved} moved, {result.AlreadyInPlace} already in place, {result.Failed} failed, {result.Skipped} skipped, {result.NotOpen} not open");
        foreach (var m in plan.Moves.Where(m => m.Note.Length > 0)) Log.Info($"  '{m.Window.Title}': {m.Note}");
        return result;
    }

    public void Forget(IEnumerable<Guid> entryIds)
    {
        var ids = entryIds.ToHashSet();
        foreach (var p in Data.Profiles) p.Windows.RemoveAll(e => ids.Contains(e.Id));
        Data.Profiles.RemoveAll(p => p.Windows.Count == 0);
        Persist();
        Log.Info($"Forgot {ids.Count} saved window(s)");
    }

    /// <summary>The saved profile for these exact monitors, or else the one sharing the most monitors with them.</summary>
    public (Profile? Profile, bool IsFallback) PickProfile(DisplaySet displays)
    {
        var exact = Data.Profiles.FirstOrDefault(p => p.Key == displays.ProfileKey);
        if (exact != null) return (exact, false);
        var ids = displays.Displays.Select(d => d.Id).ToHashSet();
        var best = Data.Profiles
            .Select(p => (p, shared: p.Monitors.Count(m => ids.Contains(m.Id))))
            .Where(x => x.shared > 0)
            .OrderByDescending(x => x.shared).ThenByDescending(x => x.p.LastSaved)
            .Select(x => x.p)
            .FirstOrDefault();
        return (best, best != null);
    }

    public static string Describe(Profile profile) => string.Join(" + ", profile.Monitors.Select(m => m.Name));

    private void Persist()
    {
        JsonFile.Save(_paths.Layouts, Data);
        Changed?.Invoke();
    }

    private static void Fill(WindowEntry entry, LiveWindow window, DateTime now)
    {
        entry.Title = window.Title;
        entry.State = window.State;
        entry.Rect = window.Rect;
        entry.RestoreToMaximized = window.RestoreToMaximized;
        entry.MonitorId = window.MonitorId;
        entry.LastSaved = now;
        Bind(entry, window);
    }

    private static void Bind(WindowEntry entry, LiveWindow window)
    {
        entry.LastHwnd = window.Hwnd.ToInt64();
        entry.LastPid = window.Pid;
        entry.LastSeen = DateTime.Now;
    }

    /// <summary>
    /// Saved rectangles are relative to the monitor geometry stored in the profile. When a monitor's
    /// position or resolution changed since then, shift the entries of closed windows along with it.
    /// </summary>
    private static void KeepUnmatchedEntriesConsistent(Profile profile, DisplaySet displays, HashSet<WindowEntry> matched)
    {
        foreach (var entry in profile.Windows.Where(e => !matched.Contains(e)))
        {
            var oldMonitor = profile.Monitors.FirstOrDefault(m => m.Id == entry.MonitorId);
            var newMonitor = displays.ById(entry.MonitorId);
            if (oldMonitor == null || newMonitor == null || oldMonitor.WorkArea == newMonitor.WorkArea) continue;
            entry.Rect = PlacementPlanner.MapBetween(entry.Rect, oldMonitor.WorkArea, newMonitor.WorkArea);
        }
    }
}
