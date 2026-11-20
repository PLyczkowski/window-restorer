using System.Runtime.InteropServices;
using WindowRestorer.Native;

namespace WindowRestorer.Core;

/// <param name="Id">Stable hardware id of the monitor (model + connection), e.g. "DEL40B6#5&amp;1a2b&amp;0&amp;UID4352".</param>
/// <param name="Handle">Windows monitor handle; only valid until the display setup changes.</param>
public sealed record DisplayInfo(string Id, string Name, PixelRect Bounds, PixelRect WorkArea, bool Primary, IntPtr Handle = default)
{
    public SavedMonitor ToSaved() => new() { Id = Id, Name = Name, Bounds = Bounds, WorkArea = WorkArea, Primary = Primary };
}

/// <summary>The monitors connected right now.</summary>
public sealed class DisplaySet(IReadOnlyList<DisplayInfo> displays)
{
    public IReadOnlyList<DisplayInfo> Displays { get; } = displays;

    /// <summary>Which saved profile belongs to this set of monitors.</summary>
    public string ProfileKey => MakeProfileKey(Displays.Select(d => d.Id));

    /// <summary>Changes whenever a monitor is added/removed or moved/resized; used to detect when the setup has settled.</summary>
    public string Signature => string.Join(";", Displays.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => $"{d.Id}@{d.Bounds}"));

    public DisplayInfo Primary => Displays.FirstOrDefault(d => d.Primary) ?? Displays[0];

    public DisplayInfo? ById(string id) => Displays.FirstOrDefault(d => d.Id == id);

    public DisplayInfo? ByHandle(IntPtr handle) => Displays.FirstOrDefault(d => d.Handle == handle);

    /// <summary>Monitor that contains most of <paramref name="rect"/>, or the closest one.</summary>
    public DisplayInfo ForRect(PixelRect rect)
    {
        var best = Displays.MaxBy(d => Area(d.Bounds.Intersect(rect)))!;
        if (Area(best.Bounds.Intersect(rect)) > 0) return best;
        return Displays.MinBy(d => DistanceSquared(d.Bounds, rect.CenterX, rect.CenterY))!;
    }

    public static string MakeProfileKey(IEnumerable<string> ids) => string.Join("|", ids.Order(StringComparer.Ordinal));

    private static long Area(PixelRect r) => (long)r.Width * r.Height;

    private static long DistanceSquared(PixelRect r, int x, int y)
    {
        long dx = x < r.Left ? r.Left - x : x > r.Right ? x - r.Right : 0;
        long dy = y < r.Top ? r.Top - y : y > r.Bottom ? y - r.Bottom : 0;
        return dx * dx + dy * dy;
    }

    /// <summary>Reads the current monitors from Windows.</summary>
    public static DisplaySet Capture()
    {
        var list = new List<DisplayInfo>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                Log.Warn($"GetMonitorInfo failed for monitor handle {hMonitor}");
                return true;
            }
            var (id, name) = DescribeMonitor(info.szDevice);
            list.Add(new DisplayInfo(id, name, ToPixelRect(info.rcMonitor), ToPixelRect(info.rcWork),
                (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0, hMonitor));
            return true;
        }, IntPtr.Zero);

        // Ids must be unique for profile keys; two identical monitors on the same port pattern are already distinct by UID.
        var duplicates = list.GroupBy(d => d.Id).Where(g => g.Count() > 1).ToList();
        foreach (var group in duplicates)
        {
            Log.Warn($"Two monitors report the same id {group.Key}; telling them apart by position");
            foreach (var d in group.ToList())
            {
                list[list.IndexOf(d)] = d with { Id = $"{d.Id}@{d.Bounds.Left},{d.Bounds.Top}" };
            }
        }
        if (list.Count == 0) throw new InvalidOperationException("Windows reported no monitors.");
        return new DisplaySet(list);
    }

    public static PixelRect ToPixelRect(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>Gets the monitor hardware id(s) behind a GDI display like \\.\DISPLAY1.</summary>
    private static (string Id, string Name) DescribeMonitor(string gdiDevice)
    {
        var ids = new List<string>();
        var names = new List<string>();
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!NativeMethods.EnumDisplayDevices(gdiDevice, i, ref dd, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME)) break;
            if ((dd.StateFlags & NativeMethods.DISPLAY_DEVICE_ACTIVE) == 0 || string.IsNullOrEmpty(dd.DeviceID)) continue;
            ids.Add(ShortenDeviceId(dd.DeviceID));
            names.Add(dd.DeviceString);
        }
        var shortGdi = gdiDevice.Replace(@"\\.\", "");
        if (ids.Count == 0)
        {
            // Virtual/remote displays may not expose a monitor device; fall back to the display name.
            return ($"gdi:{shortGdi}", shortGdi);
        }
        var model = ids[0].Split('#')[0];
        return (string.Join("+", ids), $"{model} ({shortGdi})");
    }

    /// <summary>\\?\DISPLAY#DEL40B6#5&amp;1a2b&amp;0&amp;UID4352#{e6f0...} → DEL40B6#5&amp;1a2b&amp;0&amp;UID4352</summary>
    private static string ShortenDeviceId(string deviceId)
    {
        var parts = deviceId.Split('#');
        return parts.Length >= 3 ? $"{parts[1]}#{parts[2]}" : deviceId;
    }
}
