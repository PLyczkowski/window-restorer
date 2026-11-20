using WindowRestorer.Core;

namespace WindowRestorer.Tests;

/// <summary>
/// Monitor changes can't be simulated with real hardware in a test, so the fallback rules are
/// checked against made-up monitor setups.
/// </summary>
public sealed class PlannerTests
{
    private static readonly DisplayInfo Left = new("LEFT#1", "Left", PixelRect.FromSize(0, 0, 1920, 1080), PixelRect.FromSize(0, 0, 1920, 1040), true);
    private static readonly DisplayInfo Right = new("RIGHT#1", "Right", PixelRect.FromSize(1920, 0, 2560, 1440), PixelRect.FromSize(1920, 0, 2560, 1400), false);

    private static Profile ProfileWith(WindowEntry entry, params DisplayInfo[] monitors) => new()
    {
        Key = DisplaySet.MakeProfileKey(monitors.Select(m => m.Id)),
        Monitors = monitors.Select(m => m.ToSaved()).ToList(),
        Windows = [entry],
    };

    private static WindowEntry Entry(PixelRect rect, string monitorId, WindowShowState state = WindowShowState.Normal) => new()
    {
        ExePath = @"C:\app.exe", ClassName = "Cls", Title = "Doc - App", MonitorId = monitorId, Rect = rect, State = state,
    };

    private static LiveWindow Live(PixelRect rect, string monitorId) =>
        new(1, 1, @"C:\app.exe", "Cls", "Doc - App", WindowShowState.Normal, rect, false, monitorId, Resizable: true, Hung: false, UsesWorkspaceCoords: true);

    private static PlannedMove Plan(Profile profile, DisplaySet now, MissingMonitorPolicy policy = MissingMonitorPolicy.MoveToNearest) =>
        PlacementPlanner.Plan(new WindowMatch(Live(PixelRect.FromSize(10, 10, 300, 200), Left.Id), profile.Windows[0], MatchKind.ExactTitle), profile, now, policy);

    [Fact]
    public void Same_monitors_restore_exact_position()
    {
        var saved = PixelRect.FromSize(2100, 100, 800, 600);
        var plan = Plan(ProfileWith(Entry(saved, Right.Id), Left, Right), new DisplaySet([Left, Right]));
        Assert.Equal(PlanAction.Move, plan.Action);
        Assert.Equal(saved, plan.TargetRect);
        Assert.Equal(Right.Id, plan.TargetMonitorId);
    }

    [Fact]
    public void Missing_monitor_moves_window_to_nearest_connected_monitor()
    {
        var plan = Plan(ProfileWith(Entry(PixelRect.FromSize(2100, 100, 800, 600), Right.Id), Left, Right), new DisplaySet([Left]));
        Assert.Equal(PlanAction.Move, plan.Action);
        Assert.Equal(Left.Id, plan.TargetMonitorId);
        Assert.True(Left.WorkArea.Inflate(PlacementPlanner.BorderTolerance).Contains(plan.TargetRect), plan.TargetRect.ToString());
    }

    [Fact]
    public void Missing_monitor_with_leave_policy_leaves_window_alone()
    {
        var plan = Plan(ProfileWith(Entry(PixelRect.FromSize(2100, 100, 800, 600), Right.Id), Left, Right), new DisplaySet([Left]), MissingMonitorPolicy.Leave);
        Assert.Equal(PlanAction.LeaveMissingMonitor, plan.Action);
    }

    [Fact]
    public void Changed_resolution_scales_position_relative_to_the_monitor()
    {
        var lowRes = Right with { Bounds = PixelRect.FromSize(1920, 0, 1280, 720), WorkArea = PixelRect.FromSize(1920, 0, 1280, 700) };
        var plan = Plan(ProfileWith(Entry(PixelRect.FromSize(1920 + 1280, 700, 1280, 700), Right.Id), Left, Right), new DisplaySet([Left, lowRes]));
        Assert.Equal(PixelRect.FromSize(1920 + 640, 350, 640, 350), plan.TargetRect);
    }

    [Fact]
    public void Moved_monitor_shifts_window_with_it()
    {
        var movedRight = Right with { Bounds = Right.Bounds.Offset(0, 300), WorkArea = Right.WorkArea.Offset(0, 300) };
        var plan = Plan(ProfileWith(Entry(PixelRect.FromSize(2100, 100, 800, 600), Right.Id), Left, Right), new DisplaySet([Left, movedRight]));
        Assert.Equal(PixelRect.FromSize(2100, 400, 800, 600), plan.TargetRect);
    }

    [Fact]
    public void Window_larger_than_new_monitor_is_shrunk_to_fit()
    {
        var plan = Plan(ProfileWith(Entry(PixelRect.FromSize(1920, 0, 2560, 1400), Right.Id), Left, Right), new DisplaySet([Left]));
        Assert.True(plan.TargetRect.Width <= Left.WorkArea.Width + 2 * PlacementPlanner.BorderTolerance);
        Assert.True(plan.TargetRect.Height <= Left.WorkArea.Height + 2 * PlacementPlanner.BorderTolerance);
    }

    [Fact]
    public void Fallback_profile_is_the_one_sharing_most_monitors()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WindowRestorerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new LayoutService(new DataPaths(dir), () => new AppSettings());
            var docked = ProfileWith(Entry(PixelRect.FromSize(0, 0, 100, 100), Left.Id), Left, Right);
            var other = ProfileWith(Entry(PixelRect.FromSize(0, 0, 100, 100), "OTHER#1"), new DisplayInfo("OTHER#1", "Other", Left.Bounds, Left.WorkArea, true));
            service.Data.Profiles.AddRange([other, docked]);

            var (picked, fallback) = service.PickProfile(new DisplaySet([Left]));
            Assert.Same(docked, picked);
            Assert.True(fallback);
            Assert.Null(service.PickProfile(new DisplaySet([new DisplayInfo("NEW#1", "New", Left.Bounds, Left.WorkArea, true)])).Profile);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
