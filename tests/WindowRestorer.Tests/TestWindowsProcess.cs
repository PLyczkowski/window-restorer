using System.Diagnostics;
using WindowRestorer.Core;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WindowRestorer.Tests;

/// <summary>Runs the TestWindows helper app, which opens real windows with known titles and positions.</summary>
public sealed class TestWindowsProcess : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    private TestWindowsProcess(Process process) => _process = process;

    public int Pid => _process.Id;

    public static TestWindowsProcess Start(params string[] windowSpecs)
    {
        var exe = Path.Combine(AppContext.BaseDirectory.Replace("WindowRestorer.Tests", "TestWindows"), "TestWindows.exe");
        Assert.True(File.Exists(exe), $"Helper not built at {exe}");
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var spec in windowSpecs) psi.ArgumentList.Add(spec);
        var process = Process.Start(psi)!;
        var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        Assert.Equal("ready", line);
        var helper = new TestWindowsProcess(process);
        helper.WaitUntil(ws => ws.Count == windowSpecs.Length, "all helper windows visible");
        return helper;
    }

    public List<LiveWindow> Windows() => WindowEnumerator.Enumerate(DisplaySet.Capture()).Where(w => w.Pid == Pid).ToList();

    public LiveWindow Window(string title) => Windows().Single(w => w.Title == title);

    public void WaitUntil(Func<List<LiveWindow>, bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition(Windows())) return;
            Thread.Sleep(50);
        }
        Assert.Fail($"Timed out waiting for: {what}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        _process.WaitForExit(5000);
        _process.Dispose();
    }
}
