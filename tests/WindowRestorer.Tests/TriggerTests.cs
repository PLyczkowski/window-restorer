using System.Diagnostics;
using WindowRestorer.App;
using WindowRestorer.Core;
using WindowRestorer.Native;

namespace WindowRestorer.Tests;

/// <summary>
/// End-to-end through the automatic triggers: the app's hidden event window gets the same message Windows
/// broadcasts when monitors change, and the real <see cref="TriggerController"/> handles it.
/// </summary>
public sealed class TriggerTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "WindowRestorerTests", Guid.NewGuid().ToString("N"));
    private TestWindowsProcess? _helper;

    [Fact]
    public void Monitor_change_restores_windows_even_when_the_same_monitors_are_back()
    {
        // The controller uses WinForms timers and awaits; give this thread the app's kind of message loop.
        // Set up here, not in the constructor: xUnit may run the constructor on another thread.
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        try
        {
            RunMonitorChangeScenario();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }

    private void RunMonitorChangeScenario()
    {
        var area = DisplaySet.Capture().Primary.WorkArea;
        var home = PixelRect.FromSize(area.Left + 80, area.Top + 80, 420, 300);
        var piledUp = PixelRect.FromSize(area.Left + 600, area.Top + 300, 420, 300);
        _helper = TestWindowsProcess.Start($"WR trigger@{home.Left},{home.Top},{home.Width},{home.Height}");
        var settings = new AppSettings();
        var layouts = new LayoutService(new DataPaths(_dataDir), () => settings, w => w.Pid == _helper.Pid);
        layouts.Remember("test");

        using var events = new SystemEventsWindow("test-" + Guid.NewGuid().ToString("N"));
        using var triggers = new TriggerController(layouts, () => settings, events);

        // What a monitor switching off and on again looks like: Windows moves the window, then the
        // monitors end up exactly as before.
        var w = _helper.Window("WR trigger");
        NativeMethods.SetWindowPos(w.Hwnd, IntPtr.Zero, piledUp.Left, piledUp.Top, piledUp.Width, piledUp.Height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        NativeMethods.PostMessage(events.Handle, NativeMethods.WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero);

        PumpUntil(() => _helper.Window("WR trigger").Rect.NearlyEquals(home), TimeSpan.FromSeconds(15));

        // The piled-up position must not have been auto-saved over the good one.
        Assert.True(layouts.Data.Profiles.Single().Windows.Single().Rect.NearlyEquals(home));
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            Application.DoEvents();
            if (condition()) return;
            Thread.Sleep(20);
        }
        Assert.Fail($"Condition not met within {timeout.TotalSeconds} s");
    }

    public void Dispose()
    {
        _helper?.Dispose();
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }
}
