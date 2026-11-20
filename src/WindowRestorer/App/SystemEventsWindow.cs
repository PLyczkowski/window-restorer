using System.Runtime.InteropServices;
using WindowRestorer.Core;
using WindowRestorer.Native;

namespace WindowRestorer.App;

/// <summary>
/// Hidden top-level window that receives the system broadcasts this app reacts to: shutdown,
/// display changes, sleep/wake and screens turning on/off. (Message-only windows don't get broadcasts.)
/// </summary>
public sealed class SystemEventsWindow : NativeWindow, IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    /// <summary>Sent by a second copy of the app (started while this one runs) to open Settings.</summary>
    private static readonly int ShowSettingsMessage = NativeMethods.RegisterWindowMessage("WindowRestorer.ShowSettings");
    private IntPtr _displayStateRegistration;

    public static string CaptionFor(string instanceId) => $"WindowRestorer.Events.{instanceId}";

    /// <summary>Asks the running copy with this instance id to show its Settings window. False if none is running.</summary>
    public static bool RequestShowSettings(string instanceId)
    {
        var hwnd = NativeMethods.FindWindow(null, CaptionFor(instanceId));
        return hwnd != IntPtr.Zero && NativeMethods.PostMessage(hwnd, ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public event Action? ShuttingDown;
    public event Action? DisplayChanged;
    public event Action? Suspending;
    public event Action? Resumed;
    /// <summary>True when the screens turned on (or dimmed), false when they turned off.</summary>
    public event Action<bool>? ScreensPowerChanged;
    public event Action? ShowSettingsRequested;

    public SystemEventsWindow(string instanceId)
    {
        CreateHandle(new CreateParams { Caption = CaptionFor(instanceId), Style = WS_POPUP, ExStyle = (int)NativeMethods.WS_EX_TOOLWINDOW });
        // When running as admin, Windows blocks messages from normal programs unless allowed explicitly.
        if (!NativeMethods.ChangeWindowMessageFilterEx(Handle, ShowSettingsMessage, NativeMethods.MSGFLT_ALLOW, IntPtr.Zero))
        {
            Log.Warn($"Couldn't allow the 'show settings' message (error {Marshal.GetLastWin32Error()}); starting the app again won't open Settings");
        }
        var guid = NativeMethods.GUID_CONSOLE_DISPLAY_STATE;
        _displayStateRegistration = NativeMethods.RegisterPowerSettingNotification(Handle, ref guid, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_displayStateRegistration == IntPtr.Zero)
        {
            Log.Error($"Couldn't subscribe to screen on/off notifications (error {Marshal.GetLastWin32Error()}); restore after screens turn on won't work");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ShowSettingsMessage)
        {
            ShowSettingsRequested?.Invoke();
            return;
        }
        switch (m.Msg)
        {
            case NativeMethods.WM_QUERYENDSESSION:
                // Must finish before returning: Windows starts closing programs right after.
                ShuttingDown?.Invoke();
                m.Result = 1;
                return;
            case NativeMethods.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                break;
            case NativeMethods.WM_POWERBROADCAST:
                HandlePower(m);
                m.Result = 1;
                return;
        }
        base.WndProc(ref m);
    }

    private void HandlePower(Message m)
    {
        switch (m.WParam.ToInt32())
        {
            case NativeMethods.PBT_APMSUSPEND:
                Suspending?.Invoke();
                break;
            case NativeMethods.PBT_APMRESUMEAUTOMATIC:
            case NativeMethods.PBT_APMRESUMESUSPEND:
                Resumed?.Invoke();
                break;
            case NativeMethods.PBT_POWERSETTINGCHANGE:
                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
                if (setting.PowerSetting == NativeMethods.GUID_CONSOLE_DISPLAY_STATE)
                {
                    // 0 = off, 1 = on, 2 = dimmed.
                    ScreensPowerChanged?.Invoke(setting.Data != 0);
                }
                break;
        }
    }

    public void Dispose()
    {
        if (_displayStateRegistration != IntPtr.Zero)
        {
            NativeMethods.UnregisterPowerSettingNotification(_displayStateRegistration);
            _displayStateRegistration = IntPtr.Zero;
        }
        DestroyHandle();
    }
}
