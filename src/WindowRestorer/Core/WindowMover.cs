using WindowRestorer.Native;

namespace WindowRestorer.Core;

/// <summary>Applies planned placements to real windows.</summary>
public static class WindowMover
{
    /// <summary>
    /// Asks the window to take its target placement. The request is posted to the window's own program
    /// (async), so a slow program can't freeze this app; call <see cref="IsInPlace"/> a moment later to check.
    /// </summary>
    public static bool Apply(PlannedMove move, DisplaySet displays)
    {
        var hwnd = move.Window.Hwnd;
        if (!NativeMethods.IsWindow(hwnd))
        {
            Log.Warn($"'{move.Window.Title}' closed before it could be moved");
            return false;
        }

        var placement = WINDOWPLACEMENT.Create();
        if (!NativeMethods.GetWindowPlacement(hwnd, ref placement))
        {
            Log.Warn($"Can't read placement of '{move.Window.Title}' (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return false;
        }

        // A maximized window ignores a new restore position until it's un-maximized, so do that first.
        if (NativeMethods.IsZoomed(hwnd))
        {
            placement.showCmd = NativeMethods.SW_SHOWNOACTIVATE;
            placement.flags = NativeMethods.WPF_ASYNCWINDOWPLACEMENT;
            if (!Set(hwnd, ref placement, move)) return false;
        }

        var target = move.Window.UsesWorkspaceCoords ? Coordinates.ScreenToWorkspace(move.TargetRect, displays) : move.TargetRect;
        placement.rcNormalPosition = new RECT { Left = target.Left, Top = target.Top, Right = target.Right, Bottom = target.Bottom };
        placement.showCmd = move.TargetState switch
        {
            WindowShowState.Normal => NativeMethods.SW_SHOWNOACTIVATE,
            WindowShowState.Maximized => NativeMethods.SW_SHOWMAXIMIZED,
            WindowShowState.Minimized => NativeMethods.SW_SHOWMINNOACTIVE,
            _ => throw new ArgumentOutOfRangeException(nameof(move), move.TargetState, "Unknown window state"),
        };
        placement.flags = NativeMethods.WPF_ASYNCWINDOWPLACEMENT |
                          (move.TargetState == WindowShowState.Minimized && move.Entry.RestoreToMaximized ? NativeMethods.WPF_RESTORETOMAXIMIZED : 0);
        return Set(hwnd, ref placement, move);
    }

    /// <summary>True if the window now has the planned state, position and monitor.</summary>
    public static bool IsInPlace(PlannedMove move, DisplaySet displays)
    {
        var now = WindowEnumerator.Read(move.Window.Hwnd, displays);
        if (now == null || now.State != move.TargetState || !now.Rect.NearlyEquals(move.TargetRect)) return false;
        return move.TargetState != WindowShowState.Maximized || now.MonitorId == move.TargetMonitorId;
    }

    private static bool Set(IntPtr hwnd, ref WINDOWPLACEMENT placement, PlannedMove move)
    {
        if (NativeMethods.SetWindowPlacement(hwnd, ref placement)) return true;
        // Typically "access denied": the window belongs to an admin program and this app isn't running as admin.
        Log.Warn($"Windows refused to move '{move.Window.Title}' ({move.Window.ExeName}), error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        return false;
    }
}
