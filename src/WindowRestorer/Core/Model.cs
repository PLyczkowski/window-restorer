using System.Text.Json.Serialization;

namespace WindowRestorer.Core;

/// <summary>Rectangle in physical screen pixels (virtual-desktop coordinates).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    [JsonIgnore] public int Width => Right - Left;
    [JsonIgnore] public int Height => Bottom - Top;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;
    [JsonIgnore] public int CenterX => Left + Width / 2;
    [JsonIgnore] public int CenterY => Top + Height / 2;

    public static PixelRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    public PixelRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public PixelRect Inflate(int amount) => new(Left - amount, Top - amount, Right + amount, Bottom + amount);

    public PixelRect Intersect(PixelRect other)
    {
        var r = new PixelRect(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
        return r.IsEmpty ? default : r;
    }

    public bool Contains(PixelRect other) =>
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    public bool NearlyEquals(PixelRect other, int tolerance = 2) =>
        Math.Abs(Left - other.Left) <= tolerance && Math.Abs(Top - other.Top) <= tolerance &&
        Math.Abs(Right - other.Right) <= tolerance && Math.Abs(Bottom - other.Bottom) <= tolerance;

    public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowShowState>))]
public enum WindowShowState
{
    Normal,
    Maximized,
    Minimized,
}

/// <summary>A monitor as it was when a profile was last saved.</summary>
public sealed class SavedMonitor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required PixelRect Bounds { get; init; }
    public required PixelRect WorkArea { get; init; }
    public bool Primary { get; init; }
}

/// <summary>
/// One remembered window. Entries are never removed automatically: an entry for a window
/// that is closed stays until a matching window is saved over it or the user forgets it.
/// </summary>
public sealed class WindowEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ExePath { get; set; }
    public required string ClassName { get; set; }
    public required string Title { get; set; }
    public WindowShowState State { get; set; }
    /// <summary>Normal-state rectangle (for maximized/minimized windows: where it goes when restored).</summary>
    public PixelRect Rect { get; set; }
    /// <summary>For minimized windows: un-minimize to maximized.</summary>
    public bool RestoreToMaximized { get; set; }
    /// <summary>Monitor the window is shown on (the maximized monitor for maximized windows).</summary>
    public required string MonitorId { get; set; }
    public DateTime LastSaved { get; set; }
    public DateTime LastSeen { get; set; }
    /// <summary>Window handle + process the entry was last tied to; lets a running window keep its entry when its title changes.</summary>
    public long LastHwnd { get; set; }
    public int LastPid { get; set; }
}

/// <summary>Saved layout for one set of connected monitors.</summary>
public sealed class Profile
{
    /// <summary>Sorted monitor ids joined with '|'. Same monitors in a different arrangement or resolution share a profile.</summary>
    public required string Key { get; init; }
    public List<SavedMonitor> Monitors { get; set; } = [];
    public List<WindowEntry> Windows { get; set; } = [];
    public DateTime LastSaved { get; set; }
}

public sealed class LayoutData
{
    public int Version { get; set; } = 1;
    public List<Profile> Profiles { get; set; } = [];
}
