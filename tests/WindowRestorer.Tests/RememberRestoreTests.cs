using WindowRestorer.Core;
using WindowRestorer.Native;

namespace WindowRestorer.Tests;

/// <summary>
/// End-to-end: real windows from the TestWindows helper are remembered, disturbed and restored
/// through the same LayoutService the app uses. Only the helper's windows are touched.
/// </summary>
public sealed class RememberRestoreTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "WindowRestorerTests", Guid.NewGuid().ToString("N"));
    private readonly List<TestWindowsProcess> _helpers = [];
    private readonly HashSet<int> _helperPids = [];
    private readonly AppSettings _settings = new();
    private readonly PixelRect _area = DisplaySet.Capture().Primary.WorkArea;

    private LayoutService NewService() => new(new DataPaths(_dataDir), () => _settings, w => _helperPids.Contains(w.Pid));

    private TestWindowsProcess Launch(params (string Title, PixelRect Rect)[] windows)
    {
        var helper = TestWindowsProcess.Start(windows.Select(w => $"{w.Title}@{w.Rect.Left},{w.Rect.Top},{w.Rect.Width},{w.Rect.Height}").ToArray());
        _helpers.Add(helper);
        _helperPids.Add(helper.Pid);
        return helper;
    }

    private PixelRect Spot(int index) => PixelRect.FromSize(_area.Left + 60 + index * 90, _area.Top + 60 + index * 70, 420, 300);

    private static void MoveTo(LiveWindow w, PixelRect r) =>
        Assert.True(NativeMethods.SetWindowPos(w.Hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE));

    [Fact]
    public async Task Restore_puts_moved_and_maximized_windows_back()
    {
        var helper = Launch(("WR test A", Spot(0)), ("WR test B", Spot(1)), ("WR test C", Spot(2)));
        var service = NewService();
        var saved = service.Remember("test");
        Assert.Equal(3, saved.Added);

        MoveTo(helper.Window("WR test A"), Spot(4));
        NativeMethods.ShowWindow(helper.Window("WR test B").Hwnd, NativeMethods.SW_SHOWMAXIMIZED);
        NativeMethods.ShowWindow(helper.Window("WR test C").Hwnd, NativeMethods.SW_SHOWMINNOACTIVE);
        helper.WaitUntil(ws => ws.Single(w => w.Title == "WR test B").State == WindowShowState.Maximized
                               && ws.Single(w => w.Title == "WR test C").State == WindowShowState.Minimized, "B maximized, C minimized");

        var result = await service.RestoreAsync("test");

        Assert.Equal(3, result.Moved);
        Assert.Equal(0, result.Failed);
        foreach (var (title, i) in new[] { ("WR test A", 0), ("WR test B", 1), ("WR test C", 2) })
        {
            var w = helper.Window(title);
            Assert.Equal(WindowShowState.Normal, w.State);
            Assert.True(w.Rect.NearlyEquals(Spot(i)), $"{title} at {w.Rect}, expected {Spot(i)}");
        }
    }

    [Fact]
    public async Task Restore_keeps_maximized_and_minimized_states()
    {
        var helper = Launch(("WR test max", Spot(0)), ("WR test min", Spot(1)));
        NativeMethods.ShowWindow(helper.Window("WR test max").Hwnd, NativeMethods.SW_SHOWMAXIMIZED);
        NativeMethods.ShowWindow(helper.Window("WR test min").Hwnd, NativeMethods.SW_SHOWMINNOACTIVE);
        helper.WaitUntil(ws => ws.Count(w => w.State != WindowShowState.Normal) == 2, "states applied");
        var service = NewService();
        service.Remember("test");

        NativeMethods.ShowWindow(helper.Window("WR test max").Hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.ShowWindow(helper.Window("WR test min").Hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        helper.WaitUntil(ws => ws.All(w => w.State == WindowShowState.Normal), "both normal");
        MoveTo(helper.Window("WR test min"), Spot(3));

        await service.RestoreAsync("test");

        Assert.Equal(WindowShowState.Maximized, helper.Window("WR test max").State);
        var min = helper.Window("WR test min");
        Assert.Equal(WindowShowState.Minimized, min.State);
        Assert.True(min.Rect.NearlyEquals(Spot(1)), $"restore position of minimized window is {min.Rect}, expected {Spot(1)}");
    }

    [Fact]
    public async Task Entry_of_closed_window_is_kept_and_reused_when_it_opens_again()
    {
        using (var first = Launch(("WR keep A", Spot(0)), ("WR keep B", Spot(1))))
        {
            NewService().Remember("test");
        }

        // Only A is open now; saving must update A and keep B's entry untouched.
        using (var second = Launch(("WR keep A", Spot(2))))
        {
            var result = NewService().Remember("test");
            Assert.Equal(1, result.Updated);
            Assert.Equal(1, result.KeptNotOpen);
        }

        var service = NewService();
        Assert.Equal(2, service.Data.Profiles.Single().Windows.Count);
        var third = Launch(("WR keep B", Spot(4)));
        var restore = await service.RestoreAsync("test");

        Assert.Equal(1, restore.Moved);
        Assert.Equal(1, restore.NotOpen);
        Assert.True(third.Window("WR keep B").Rect.NearlyEquals(Spot(1)));
    }

    [Fact]
    public async Task Window_whose_title_changed_is_matched_to_the_most_similar_entry()
    {
        using (Launch(("Report.docx - WR Editor", Spot(0)), ("Notes - WR Other", Spot(1))))
        {
            NewService().Remember("test");
        }

        var helper = Launch(("Budget.docx - WR Editor", Spot(3)));
        var plan = NewService().PlanRestore();
        var move = Assert.Single(plan.Moves);
        Assert.Equal(MatchKind.SimilarTitle, move.Match);
        Assert.Equal("Report.docx - WR Editor", move.Entry.Title);

        await NewService().RestoreAsync("test");
        Assert.True(helper.Window("Budget.docx - WR Editor").Rect.NearlyEquals(Spot(0)));
    }

    [Fact]
    public async Task Window_saved_off_screen_is_pulled_back_into_view()
    {
        var helper = Launch(("WR offscreen", Spot(0)));
        var service = NewService();
        service.Remember("test");
        var entry = service.Data.Profiles.Single().Windows.Single();
        entry.Rect = entry.Rect.Offset(0, 100_000);

        await service.RestoreAsync("test");

        var w = helper.Window("WR offscreen");
        Assert.True(PlacementPlanner.IsReachable(w.Rect, DisplaySet.Capture()), $"window at {w.Rect}");
    }

    public void Dispose()
    {
        foreach (var h in _helpers) h.Dispose();
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }
}
