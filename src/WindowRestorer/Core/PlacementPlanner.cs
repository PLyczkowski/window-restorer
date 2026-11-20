namespace WindowRestorer.Core;

public enum PlanAction
{
    Move,
    AlreadyInPlace,
    /// <summary>The saved monitor isn't connected and the setting says to leave such windows alone.</summary>
    LeaveMissingMonitor,
    /// <summary>The window's program isn't responding; moving it could freeze this app.</summary>
    SkipHung,
}

public sealed record PlannedMove(
    LiveWindow Window,
    WindowEntry Entry,
    MatchKind Match,
    PlanAction Action,
    WindowShowState TargetState,
    PixelRect TargetRect,
    string TargetMonitorId,
    string Note);

/// <summary>Works out where a matched window should go on the monitors connected now.</summary>
public static class PlacementPlanner
{
    /// <summary>GetWindowRect includes ~7 px invisible resize borders, so windows legitimately poke this far out of the work area.</summary>
    public const int BorderTolerance = 10;

    public static PlannedMove Plan(WindowMatch match, Profile profile, DisplaySet displays, MissingMonitorPolicy policy)
    {
        var (window, entry, kind) = match;
        var savedMonitor = profile.Monitors.FirstOrDefault(m => m.Id == entry.MonitorId);
        if (savedMonitor == null)
        {
            Log.Warn($"Entry '{entry.Title}' points at monitor {entry.MonitorId}, which isn't in its profile; using the monitor under it");
        }

        var target = displays.ById(entry.MonitorId);
        var notes = new List<string>();
        if (target == null)
        {
            var reference = savedMonitor?.Bounds ?? entry.Rect;
            if (policy == MissingMonitorPolicy.Leave)
            {
                return new PlannedMove(window, entry, kind, PlanAction.LeaveMissingMonitor, entry.State, window.Rect, window.MonitorId,
                    $"Saved monitor {savedMonitor?.Name ?? entry.MonitorId} isn't connected");
            }
            target = displays.Displays.MinBy(d => CenterDistanceSquared(d.Bounds, reference))!;
            notes.Add($"saved monitor {savedMonitor?.Name ?? entry.MonitorId} isn't connected, using {target.Name}");
        }

        var sourceArea = savedMonitor?.WorkArea ?? target.WorkArea;
        var unchangedMonitor = savedMonitor != null && savedMonitor.Id == target.Id && sourceArea == target.WorkArea;
        var rect = MapBetween(entry.Rect, sourceArea, target.WorkArea);
        if (!unchangedMonitor)
        {
            if (savedMonitor?.Id == target.Id) notes.Add("monitor layout changed, position scaled");
            rect = ClampInto(rect, target.WorkArea, window.Resizable);
        }
        else if (!IsReachable(rect, displays))
        {
            rect = ClampInto(rect, target.WorkArea, window.Resizable);
            notes.Add("was off-screen, pulled into view");
        }

        if (window.Hung)
        {
            return new PlannedMove(window, entry, kind, PlanAction.SkipHung, entry.State, rect, target.Id, "Program isn't responding");
        }

        var inPlace = window.State == entry.State && window.Rect.NearlyEquals(rect) &&
                      (entry.State != WindowShowState.Maximized || window.MonitorId == target.Id);
        return new PlannedMove(window, entry, kind, inPlace ? PlanAction.AlreadyInPlace : PlanAction.Move,
            entry.State, rect, target.Id, string.Join("; ", notes));
    }

    /// <summary>Moves/scales a rectangle from one work area to another, keeping its relative position.</summary>
    public static PixelRect MapBetween(PixelRect rect, PixelRect from, PixelRect to)
    {
        if (from == to) return rect;
        var sx = (double)to.Width / from.Width;
        var sy = (double)to.Height / from.Height;
        var left = to.Left + (int)Math.Round((rect.Left - from.Left) * sx);
        var top = to.Top + (int)Math.Round((rect.Top - from.Top) * sy);
        return PixelRect.FromSize(left, top, (int)Math.Round(rect.Width * sx), (int)Math.Round(rect.Height * sy));
    }

    /// <summary>Shrinks (if resizable) and shifts the rectangle so it fits inside the work area.</summary>
    public static PixelRect ClampInto(PixelRect rect, PixelRect workArea, bool resizable)
    {
        var area = workArea.Inflate(BorderTolerance);
        var width = resizable ? Math.Min(rect.Width, area.Width) : rect.Width;
        var height = resizable ? Math.Min(rect.Height, area.Height) : rect.Height;
        var left = Math.Max(area.Left, Math.Min(rect.Left, area.Right - width));
        var top = Math.Max(area.Top, Math.Min(rect.Top, area.Bottom - height));
        return PixelRect.FromSize(left, top, width, height);
    }

    /// <summary>True if enough of the title bar is on some monitor to grab the window with the mouse.</summary>
    public static bool IsReachable(PixelRect rect, DisplaySet displays)
    {
        var titleBar = new PixelRect(rect.Left + BorderTolerance, rect.Top + BorderTolerance, rect.Right - BorderTolerance, rect.Top + BorderTolerance + 30);
        return displays.Displays.Any(d =>
        {
            var visible = d.WorkArea.Intersect(titleBar);
            return visible.Width >= 60 && visible.Height >= 10;
        });
    }

    private static long CenterDistanceSquared(PixelRect a, PixelRect b)
    {
        long dx = a.CenterX - b.CenterX, dy = a.CenterY - b.CenterY;
        return dx * dx + dy * dy;
    }
}
