using System.Text.Json.Serialization;

namespace WindowRestorer.Core;

[JsonConverter(typeof(JsonStringEnumConverter<MissingMonitorPolicy>))]
public enum MissingMonitorPolicy
{
    /// <summary>Move the window to the closest monitor that is connected now.</summary>
    MoveToNearest,
    /// <summary>Leave the window where Windows put it.</summary>
    Leave,
}

public sealed class AppSettings
{
    /// <summary>Relaunch as administrator so windows of admin programs can be moved too.</summary>
    public bool RunAsAdmin { get; set; } = true;
    /// <summary>Start at login (via a Task Scheduler task, so admin start needs no prompt).</summary>
    public bool StartWithWindows { get; set; }

    public bool SaveOnShutdown { get; set; } = true;
    /// <summary>Save after a window is moved/resized/minimized, and every <see cref="AutoSaveIntervalSeconds"/>.</summary>
    public bool AutoSave { get; set; } = true;
    public int AutoSaveIntervalSeconds { get; set; } = 60;

    public bool RestoreAtLogon { get; set; } = true;
    /// <summary>After login, keep placing newly opened windows for this many minutes.</summary>
    public int LogonWatchMinutes { get; set; } = 3;
    public bool RestoreOnDisplayChange { get; set; } = true;
    /// <summary>Restore after waking from sleep and after the screens turn back on.</summary>
    public bool RestoreOnWake { get; set; } = true;

    public MissingMonitorPolicy MissingMonitor { get; set; } = MissingMonitorPolicy.MoveToNearest;
}

/// <summary>Where the app keeps its files. Defaults to %AppData%\WindowRestorer; <c>--data-dir</c> overrides it.</summary>
public sealed class DataPaths(string folder)
{
    public string Folder { get; } = folder;
    public string Layouts => Path.Combine(Folder, "layouts.json");
    public string Settings => Path.Combine(Folder, "settings.json");
    public string LogFile => Path.Combine(Folder, "log.txt");

    public static DataPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowRestorer"));
}
