namespace TestWindows;

/// <summary>
/// Opens one plain window per argument. Argument format: "Title@left,top,width,height" (physical pixels).
/// Prints "ready" once all windows are shown, then runs until killed.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var forms = args.Select(CreateForm).ToList();
        if (forms.Count == 0) throw new ArgumentException("Pass at least one window spec: Title@left,top,width,height");
        foreach (var f in forms.Skip(1)) f.Show();
        forms[0].Shown += (_, _) =>
        {
            Console.WriteLine("ready");
            Console.Out.Flush();
        };
        Application.Run(forms[0]);
    }

    private static Form CreateForm(string spec)
    {
        var at = spec.LastIndexOf('@');
        if (at < 0) throw new ArgumentException($"Bad window spec '{spec}'");
        var n = spec[(at + 1)..].Split(',').Select(int.Parse).ToArray();
        return new Form
        {
            Text = spec[..at],
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(n[0], n[1], n[2], n[3]),
            ShowInTaskbar = true,
        };
    }
}
