using System.Text;
using WindowRestorer.Native;

namespace WindowRestorer.Core;

/// <summary>A real app window that is open right now.</summary>
/// <param name="Rect">Normal-state rectangle in screen pixels. For a normal window this is where it is now
/// (including snapped windows); for maximized/minimized windows it's where it returns when restored.</param>
/// <param name="MonitorId">Monitor the window is shown on.</param>
public sealed record LiveWindow(
    IntPtr Hwnd,
    int Pid,
    string ExePath,
    string ClassName,
    string Title,
    WindowShowState State,
    PixelRect Rect,
    bool RestoreToMaximized,
    string MonitorId,
    bool Resizable,
    bool Hung,
    bool UsesWorkspaceCoords)
{
    public string ExeName => Path.GetFileName(ExePath);
}

/// <summary>Lists the windows a user would see in Alt+Tab and reads their placement.</summary>
public static class WindowEnumerator
{
    public const string UnknownExe = "(unknown)";
    private static readonly HashSet<int> WarnedPids = [];
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    public static List<LiveWindow> Enumerate(DisplaySet displays)
    {
        var ownPid = Environment.ProcessId;
        var exeCache = new Dictionary<int, string>();
        var result = new List<LiveWindow>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var window = TryRead(hwnd, displays, ownPid, exeCache);
            if (window != null) result.Add(window);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Reads one window again (e.g. to check a restore worked). Null if it's gone or no longer qualifies.</summary>
    public static LiveWindow? Read(IntPtr hwnd, DisplaySet displays) =>
        NativeMethods.IsWindow(hwnd) ? TryRead(hwnd, displays, Environment.ProcessId, []) : null;

    private static LiveWindow? TryRead(IntPtr hwnd, DisplaySet displays, int ownPid, Dictionary<int, string> exeCache)
    {
        if (!NativeMethods.IsWindowVisible(hwnd)) return null;
        // Skip our own windows before anything else: reading the title of an own-process window that
        // belongs to another thread sends that thread a message and waits forever if it isn't responding.
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == ownPid) return null;
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return null;

        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((style & NativeMethods.WS_CHILD) != 0) return null;
        var appWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
        var toolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        if (toolWindow && !appWindow) return null;
        if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0 && !appWindow) return null;
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero && !appWindow) return null;

        var title = GetText(hwnd);
        if (title.Length == 0) return null;
        var className = GetClass(hwnd);
        if (IgnoredClasses.Contains(className)) return null;

        // Store apps live inside ApplicationFrameHost; the real app is the process of the inner CoreWindow.
        var exePid = pid;
        if (className == "ApplicationFrameWindow") exePid = FindCoreWindowPid(hwnd) ?? pid;
        if (!exeCache.TryGetValue(exePid, out var exe))
        {
            exe = GetExePath(exePid);
            exeCache[exePid] = exe;
        }

        var placement = WINDOWPLACEMENT.Create();
        if (!NativeMethods.GetWindowPlacement(hwnd, ref placement))
        {
            Log.Warn($"Can't read placement of '{title}' ({exe})");
            return null;
        }

        var state = NativeMethods.IsIconic(hwnd) ? WindowShowState.Minimized
            : NativeMethods.IsZoomed(hwnd) ? WindowShowState.Maximized
            : WindowShowState.Normal;
        var usesWorkspace = !toolWindow;
        PixelRect rect;
        if (state == WindowShowState.Normal)
        {
            NativeMethods.GetWindowRect(hwnd, out var r);
            rect = DisplaySet.ToPixelRect(r);
        }
        else
        {
            var normal = DisplaySet.ToPixelRect(placement.rcNormalPosition);
            rect = usesWorkspace ? Coordinates.WorkspaceToScreen(normal, displays) : normal;
        }
        if (rect.IsEmpty) return null;

        var monitorId = state == WindowShowState.Minimized
            ? displays.ForRect(rect).Id
            : (displays.ByHandle(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST)) ?? displays.ForRect(rect)).Id;

        return new LiveWindow(
            hwnd, pid, exe, className, title, state, rect,
            RestoreToMaximized: (placement.flags & NativeMethods.WPF_RESTORETOMAXIMIZED) != 0,
            MonitorId: monitorId,
            Resizable: (style & NativeMethods.WS_THICKFRAME) != 0,
            Hung: NativeMethods.IsHungAppWindow(hwnd),
            UsesWorkspaceCoords: usesWorkspace);
    }

    private static string GetText(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var sb = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static int? FindCoreWindowPid(IntPtr frame)
    {
        NativeMethods.GetWindowThreadProcessId(frame, out var framePid);
        int? found = null;
        NativeMethods.EnumChildWindows(frame, (child, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(child, out var childPid);
            if (childPid != framePid && GetClass(child) == "Windows.UI.Core.CoreWindow")
            {
                found = childPid;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string GetExePath(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            // Protected system processes can refuse this; the window is still matched by class and title.
            if (WarnedPids.Add(pid)) Log.Warn($"Can't read the program path of process {pid}");
            return UnknownExe;
        }
        try
        {
            var size = 1024;
            var sb = new StringBuilder(size);
            return NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : UnknownExe;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}

/// <summary>
/// Windows reports the "restored" rectangle of maximized/minimized windows in workspace coordinates
/// (screen coordinates shifted by the taskbar when it's on the top or left edge of that monitor).
/// </summary>
public static class Coordinates
{
    public static PixelRect WorkspaceToScreen(PixelRect rect, DisplaySet displays)
    {
        var d = displays.ForRect(rect);
        return rect.Offset(d.WorkArea.Left - d.Bounds.Left, d.WorkArea.Top - d.Bounds.Top);
    }

    public static PixelRect ScreenToWorkspace(PixelRect rect, DisplaySet displays)
    {
        var d = displays.ForRect(rect);
        return rect.Offset(d.Bounds.Left - d.WorkArea.Left, d.Bounds.Top - d.WorkArea.Top);
    }
}
