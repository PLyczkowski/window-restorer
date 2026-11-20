namespace WindowRestorer.Core;

public enum MatchKind
{
    /// <summary>The very same window (handle + process) the entry was last tied to; title may have changed.</summary>
    SameWindow,
    /// <summary>Same program, window type and title.</summary>
    ExactTitle,
    /// <summary>Same program and window type; best available title (restore only).</summary>
    SimilarTitle,
}

public sealed record WindowMatch(LiveWindow Window, WindowEntry Entry, MatchKind Kind);

/// <summary>
/// Pairs open windows with saved entries, one to one. Strict to loose:
/// same window → exact title → (restore only) same program with the most similar title.
/// </summary>
public static class WindowMatcher
{
    public static List<WindowMatch> Match(IReadOnlyList<WindowEntry> entries, IReadOnlyList<LiveWindow> windows, bool allowSimilar)
    {
        var matches = new List<WindowMatch>();
        var freeEntries = new HashSet<WindowEntry>(entries);
        var freeWindows = new List<LiveWindow>(windows);

        void Take(LiveWindow w, WindowEntry e, MatchKind kind)
        {
            matches.Add(new WindowMatch(w, e, kind));
            freeEntries.Remove(e);
            freeWindows.Remove(w);
        }

        foreach (var w in windows)
        {
            var e = freeEntries.FirstOrDefault(e => e.LastHwnd == w.Hwnd.ToInt64() && e.LastPid == w.Pid && SameProgram(e, w));
            if (e != null) Take(w, e, MatchKind.SameWindow);
        }

        foreach (var w in freeWindows.ToList())
        {
            var e = freeEntries
                .Where(e => SameProgram(e, w) && string.Equals(e.Title, w.Title, StringComparison.Ordinal))
                .MaxBy(e => e.LastSeen);
            if (e != null) Take(w, e, MatchKind.ExactTitle);
        }

        if (!allowSimilar) return matches;

        var candidates =
            from w in freeWindows
            from e in freeEntries
            where SameProgram(e, w)
            select (w, e, score: TitleSimilarity(w.Title, e.Title));
        foreach (var (w, e, _) in candidates.OrderByDescending(c => c.score).ThenByDescending(c => c.e.LastSeen).ToList())
        {
            if (freeWindows.Contains(w) && freeEntries.Contains(e)) Take(w, e, MatchKind.SimilarTitle);
        }
        return matches;
    }

    public static bool SameProgram(WindowEntry e, LiveWindow w) =>
        string.Equals(e.ExePath, w.ExePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(e.ClassName, w.ClassName, StringComparison.Ordinal);

    /// <summary>0..1 text similarity, plus a bonus when the last " - " part (usually the app name) matches.</summary>
    public static double TitleSimilarity(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var score = 1.0 - (double)Levenshtein(Truncate(a), Truncate(b)) / Math.Max(1, Math.Max(Truncate(a).Length, Truncate(b).Length));
        var tailA = LastSegment(a);
        if (tailA.Length > 0 && tailA == LastSegment(b)) score += 0.25;
        return score;
    }

    private static string Truncate(string s) => s.Length > 128 ? s[..128] : s;

    private static string LastSegment(string title)
    {
        var i = Math.Max(title.LastIndexOf(" - ", StringComparison.Ordinal), title.LastIndexOf(" — ", StringComparison.Ordinal));
        return i < 0 ? "" : title[(i + 3)..].Trim();
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
