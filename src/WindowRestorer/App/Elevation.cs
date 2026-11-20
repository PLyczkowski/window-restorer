using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using WindowRestorer.Core;

namespace WindowRestorer.App;

public static class Elevation
{
    private const int ErrorCancelled = 1223;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts <paramref name="exe"/> as administrator (shows the Windows security prompt). Returns null if the user said no.</summary>
    public static Process? StartElevated(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", Arguments = JoinArgs(args) };
        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {exe}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Warn($"Admin prompt for {Path.GetFileName(exe)} was declined");
            return null;
        }
    }

    public static string JoinArgs(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    private static string Quote(string arg) => arg.Length > 0 && !arg.Any(c => c is ' ' or '"' or '\t') ? arg : $"\"{arg.Replace("\"", "\\\"")}\"";
}
