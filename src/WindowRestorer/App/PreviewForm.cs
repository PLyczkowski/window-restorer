using WindowRestorer.Core;

namespace WindowRestorer.App;

/// <summary>Shows what a restore would do, without moving anything.</summary>
public sealed class PreviewForm : Form
{
    public event Action? RestoreRequested;

    public PreviewForm(RestorePlan plan)
    {
        Text = "Window Restorer – preview restore";
        Icon = AppIcon.Load();
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 520);

        var header = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 44, Padding = new Padding(10, 10, 10, 0),
            Text = plan.Profile == null
                ? "No layout saved for the monitors connected now. Use Remember now first."
                : $"Saved layout: {LayoutService.Describe(plan.Profile)}" +
                  (plan.IsFallbackProfile ? "   (no layout for exactly these monitors; using the closest one)" : ""),
        };

        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, ShowGroups = true };
        list.Columns.Add("Action", 120);
        list.Columns.Add("Program", 120);
        list.Columns.Add("Title", 240);
        list.Columns.Add("Now", 150);
        list.Columns.Add("Restore to", 160);
        list.Columns.Add("Matched by", 100);
        list.Columns.Add("Note", 240);
        var moving = new ListViewGroup("Open windows with a saved spot");
        var notOpen = new ListViewGroup("Saved but not open (kept for later)");
        var unsaved = new ListViewGroup("Open but not saved yet");
        list.Groups.AddRange([moving, notOpen, unsaved]);

        foreach (var m in plan.Moves.OrderBy(m => m.Action))
        {
            list.Items.Add(new ListViewItem([
                Describe(m.Action), m.Window.ExeName, m.Window.Title, $"{m.Window.State} {m.Window.Rect}",
                $"{m.TargetState} {m.TargetRect}", Describe(m.Match), m.Note,
            ], moving));
        }
        foreach (var e in plan.NotOpen)
        {
            list.Items.Add(new ListViewItem(["–", Path.GetFileName(e.ExePath), e.Title, "", $"{e.State} {e.Rect}", "", $"last seen {e.LastSeen:g}"], notOpen));
        }
        foreach (var w in plan.Unmatched)
        {
            list.Items.Add(new ListViewItem(["–", w.ExeName, w.Title, $"{w.State} {w.Rect}", "", "", ""], unsaved));
        }

        var restore = new Button { Text = "Restore now", AutoSize = true, Enabled = plan.ToMove.Any() };
        restore.Click += (_, _) =>
        {
            RestoreRequested?.Invoke();
            Close();
        };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange([close, restore]);
        CancelButton = close;

        Controls.Add(list);
        Controls.Add(header);
        Controls.Add(buttons);
    }

    private static string Describe(PlanAction action) => action switch
    {
        PlanAction.Move => "Move",
        PlanAction.AlreadyInPlace => "Already in place",
        PlanAction.LeaveMissingMonitor => "Leave (monitor gone)",
        PlanAction.SkipHung => "Skip (not responding)",
        _ => action.ToString(),
    };

    private static string Describe(MatchKind kind) => kind switch
    {
        MatchKind.SameWindow => "same window",
        MatchKind.ExactTitle => "title",
        MatchKind.SimilarTitle => "similar title",
        _ => kind.ToString(),
    };
}
