using System.Runtime.CompilerServices;
using WindowRestorer.Native;

namespace WindowRestorer.Tests;

internal static class TestSetup
{
    /// <summary>The test runner has no app manifest, so switch it to the app's per-monitor DPI mode to get the same real-pixel coordinates.</summary>
    [ModuleInitializer]
    internal static void Init() => DpiAwareness.UsePerMonitorV2();
}
