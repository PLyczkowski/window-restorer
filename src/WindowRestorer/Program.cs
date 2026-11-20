using System.Security.Cryptography;
using System.Text;
using WindowRestorer.App;
using WindowRestorer.Core;
using WindowRestorer.Native;

namespace WindowRestorer;

internal static class Program
{
    /// <summary>
    /// Arguments: <c>--logon</c> (started by the login task: place windows as they open),
    /// <c>--data-dir &lt;folder&gt;</c> (keep settings/layouts/log there instead of %AppData%\WindowRestorer).
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var logon = args.Contains("--logon");
        var dataDirIndex = Array.IndexOf(args, "--data-dir");
        var paths = dataDirIndex >= 0 && dataDirIndex + 1 < args.Length ? new DataPaths(Path.GetFullPath(args[dataDirIndex + 1])) : DataPaths.Default();
        var passThroughArgs = dataDirIndex >= 0 ? new[] { "--data-dir", paths.Folder } : [];

        Directory.CreateDirectory(paths.Folder);
        Log.Init(paths.LogFile);
        ApplicationConfiguration.Initialize();

        // One copy per data folder. Starting it again opens the running copy's Settings.
        var instanceId = Hash(paths.Folder);
        var mutex = TryBecomeOnlyInstance(instanceId);
        if (mutex == null)
        {
            Log.Info("Already running; asking it to show Settings");
            if (!logon && !SystemEventsWindow.RequestShowSettings(instanceId))
            {
                MessageBox.Show("Window Restorer is already running. Look for its icon in the taskbar tray.", "Window Restorer");
            }
            return 0;
        }

        try
        {
            var settings = JsonFile.Load<AppSettings>(paths.Settings);
            var adminDeclined = false;
            if (settings.RunAsAdmin && !Elevation.IsElevated)
            {
                // Hand over to an admin copy; release the lock first so it can take it.
                mutex.ReleaseMutex();
                mutex.Dispose();
                if (Elevation.StartElevated(Environment.ProcessPath!, args) != null) return 0;
                adminDeclined = true;
                mutex = TryBecomeOnlyInstance(instanceId);
                if (mutex == null) return 0;
            }

            // Ask to hear about shutdown before other programs, so their windows are still open when we save.
            if (!NativeMethods.SetProcessShutdownParameters(0x3FF, NativeMethods.SHUTDOWN_NORETRY))
            {
                Log.Warn("Couldn't ask to be notified first at shutdown; some windows may already be closed when saving");
            }

            Application.ThreadException += (_, e) => Log.Error("Unexpected error", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unexpected fatal error", e.ExceptionObject as Exception);

            Log.Info($"Started{(logon ? " at login" : "")}, admin: {Elevation.IsElevated}, data: {paths.Folder}");
            Application.Run(new TrayApp(paths, instanceId, settings, logon, adminDeclined, passThroughArgs));
            Log.Info("Exited");
            return 0;
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    /// <summary>Takes the single-instance lock, or returns null if another copy holds it.</summary>
    private static Mutex? TryBecomeOnlyInstance(string instanceId)
    {
        try
        {
            var mutex = new Mutex(true, "Local\\WindowRestorer-" + instanceId, out var created);
            if (created) return mutex;
            mutex.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null; // Held by an admin copy, which a normal copy isn't allowed to open.
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..16];
}
