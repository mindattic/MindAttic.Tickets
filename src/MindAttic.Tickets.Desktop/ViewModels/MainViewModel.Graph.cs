using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>One ticket drawn on the Graph tab (M.TIX-T315): placed by <see cref="GraphLayout"/>, outlined in its repo's colour, sized by
/// its graph weight, with its status glyph and type as a badge.</summary>
public sealed class GraphNodeViewModel(LaidNode laid, bool isFocus, bool hasChildren, bool isCollapsed)
{
    private const double MinWidth = 150;

    public string Id => laid.Node.Id;
    public string Caption => laid.Node.Caption;
    public double Width => MinWidth + laid.Size;
    public double Height => laid.Size + 8;
    public double Left => laid.X - Width / 2;
    public double Top => laid.Y - Height / 2;
    public bool IsFocus => isFocus;
    public bool HasChildren => hasChildren;
    public bool IsCollapsed => isCollapsed;
    public string ToggleText => isCollapsed ? "+" : "−";
    public bool IsOpen => laid.Node.IsOpen;
    public Brush RepoBrush { get; } = Frozen(GraphLayout.RepoColour(laid.Node.Repo));
    public Thickness Outline => new(isFocus ? 4 : 2);
    public string Badge => (laid.Node.Status switch { "open" => "● ", "closed" => "✓ ", null => "? ", _ => "– " }) + (laid.Node.Type ?? "not a ticket")
                           + (laid.Weight > 0 ? $" · weight {laid.Weight}" : "");
    public string Tip => $"{Id} ({laid.Node.Repo ?? "not a ticket"}): {laid.Node.Summary}";
    public string SpokenName => $"{Caption}, {laid.Node.Status ?? "not a ticket"}" + (laid.Weight > 0 ? $", weight {laid.Weight}" : "") + (isFocus ? ", focused" : ": focus it and open its details");

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>One link drawn on the Graph tab: requires a solid arrow, part-of a thick parent → child tree line, related dashed, the rest dotted.</summary>
public sealed class GraphEdgeViewModel(LaidEdge laid)
{
    private static readonly DoubleCollection Solid = Freeze([]), Dashed = Freeze([6, 4]), Dotted = Freeze([1.5, 3]);

    // part-of is stored child → parent; it's drawn from the parent down to the child.
    private bool Tree => laid.Edge.Kind == TicketsDb.PartOf;
    public double X1 => Tree ? laid.X2 : laid.X1;
    public double Y1 => Tree ? laid.Y2 : laid.Y1;
    public double X2 => Tree ? laid.X1 : laid.X2;
    public double Y2 => Tree ? laid.Y1 : laid.Y2;
    public double Thickness => Tree ? 3.5 : 1.5;
    public DoubleCollection Dash => laid.Edge.Kind switch { TicketsDb.RequiresKind or TicketsDb.PartOf => Solid, "related" => Dashed, _ => Dotted };
    public bool HasArrow => laid.Edge.Kind is not (TicketsDb.PartOf or "related");
    public string Tip => $"{laid.Edge.From} {laid.Edge.Kind} {laid.Edge.To}";

    /// <summary>The arrowhead at the target end, a small triangle pulled back from the centre so it shows beside the box.</summary>
    public PointCollection Arrow
    {
        get
        {
            var (dx, dy) = (X2 - X1, Y2 - Y1);
            var len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var (ux, uy) = (dx / len, dy / len);
            var tip = new System.Windows.Point(X2 - ux * 34, Y2 - uy * 34);
            var points = new PointCollection
            {
                tip,
                new(tip.X - ux * 11 + uy * 5, tip.Y - uy * 11 - ux * 5),
                new(tip.X - ux * 11 - uy * 5, tip.Y - uy * 11 + ux * 5),
            };
            points.Freeze();
            return points;
        }
    }

    private static DoubleCollection Freeze(double[] values)
    {
        var c = new DoubleCollection(values);
        c.Freeze();
        return c;
    }
}

/// <summary>
/// The Graph tab (M.TIX-T315, Ctrl+8): the ticket node graph drawn around a focused ticket, <see cref="GraphHops"/> links out, any
/// kind or one (<see cref="GraphKind"/>), narrowed by the filter bar (repo, status, type, label, search); part-of trees laid out
/// in rows, the rest force-directed, the same way every refresh. Clicking a ticket focuses it and opens its details; a parent's
/// − / + collapses or expands what's part of it. A text list of the same links sits under it for screen readers.
/// </summary>
public sealed partial class MainViewModel
{
    public const string GraphView = "Graph";
    public const string AnyKind = "Every kind";

    private string? graphFocus;
    private int graphHops = 2;
    private string graphKind = AnyKind, graphSummary = "";
    private double graphWidth, graphHeight;
    private readonly HashSet<string> collapsedNodes = new(StringComparer.OrdinalIgnoreCase);
    private ICommand? graphFocusCommand, graphToggleCommand;

    public bool IsGraphView => SelectedView == GraphView;

    public IReadOnlyList<int> GraphHopChoices { get; } = [1, 2, 3, 4];
    public IReadOnlyList<string> GraphKindChoices { get; } = [AnyKind, TicketsDb.RequiresKind, TicketsDb.PartOf, "related", "other"];

    /// <summary>The ticket the graph is drawn around: the one the detail pane shows, else the heaviest open ticket.</summary>
    public string? GraphFocus { get => graphFocus; set { if (Set(ref graphFocus, string.IsNullOrWhiteSpace(value) ? null : value.Trim())) ApplyFilters(); } }

    public int GraphHops { get => graphHops; set { if (Set(ref graphHops, Math.Clamp(value, 1, 4))) ApplyFilters(); } }

    public string GraphKind { get => graphKind; set { if (Set(ref graphKind, GraphKindChoices.Contains(value) ? value : AnyKind)) ApplyFilters(); } }

    public ObservableCollection<GraphNodeViewModel> GraphNodes { get; } = [];
    public ObservableCollection<GraphEdgeViewModel> GraphEdges { get; } = [];
    public ObservableCollection<string> GraphLines { get; } = [];
    public double GraphWidth { get => graphWidth; private set => Set(ref graphWidth, value); }
    public double GraphHeight { get => graphHeight; private set => Set(ref graphHeight, value); }
    public string GraphSummary { get => graphSummary; private set => Set(ref graphSummary, value); }

    /// <summary>Focuses a ticket (its ID as the parameter) and opens its details.</summary>
    public ICommand GraphFocusCommand => graphFocusCommand ??= new RelayCommand(p =>
    {
        if (p is not string id) return;
        GraphFocus = id;
        _ = ShowDetailAsync(id);
    }, p => p is string);

    /// <summary>Collapses or expands what's part of a ticket (its ID as the parameter).</summary>
    public ICommand GraphToggleCommand => graphToggleCommand ??= new RelayCommand(p =>
    {
        if (p is not string id) return;
        if (!collapsedNodes.Remove(id)) collapsedNodes.Add(id);
        ApplyFilters();
    }, p => p is string);

    /// <summary>Draws the graph again from the snapshot, when the Graph tab is the one shown.</summary>
    private void ApplyGraph(Snapshot s, Func<TicketInfo, bool> match)
    {
        if (!IsGraphView) return;
        var graph = TicketGraph.Build(s.Tickets.Select(t => (t.Repo, t.Ticket)), s.Links, s.Tickets.Where(t => t.Name is not null).ToDictionary(t => t.Ticket.Id, t => t.Name!, StringComparer.OrdinalIgnoreCase));
        var focus = graphFocus ?? Detail?.Id;
        if (focus is null || !graph.Contains(focus))
            focus = s.Weights.Where(w => graph.Node(w.Key).IsOpen).OrderByDescending(w => w.Value.Value).ThenBy(w => w.Key, StringComparer.Ordinal).Select(w => w.Key).FirstOrDefault()
                    ?? s.Tickets.FirstOrDefault()?.Ticket.Id;
        graphFocus = focus;
        Raise(nameof(GraphFocus));
        GraphNodes.Clear();
        GraphEdges.Clear();
        GraphLines.Clear();
        if (focus is null) { GraphSummary = "No tickets."; return; }

        // What's hidden: under a collapsed parent (at any depth), or left out by the filter bar (the focus always stays).
        var hidden = collapsedNodes.SelectMany(c => graph.Descendants(c)).Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shown = s.Tickets.Where(match).Select(t => t.Ticket.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool KindOk(GraphEdge e) => GraphKind == AnyKind || (GraphKind == "other" ? e.Kind is not (TicketsDb.RequiresKind or TicketsDb.PartOf or "related") : e.Kind == GraphKind);
        bool Keep(string id) => string.Equals(id, focus, StringComparison.OrdinalIgnoreCase) || (!hidden.Contains(id) && (shown.Contains(id) || !graph.Contains(id)));
        var (nodes, edges) = graph.Neighbourhood(focus, GraphHops, e => KindOk(e) && Keep(e.From) && Keep(e.To));
        var picture = GraphLayout.Compute(nodes, edges, s.Weights);
        foreach (var e in picture.Edges) GraphEdges.Add(new GraphEdgeViewModel(e));
        foreach (var n in picture.Nodes)
            GraphNodes.Add(new GraphNodeViewModel(n, string.Equals(n.Node.Id, focus, StringComparison.OrdinalIgnoreCase),
                graph.Children(n.Node.Id).Count > 0, collapsedNodes.Contains(n.Node.Id)));
        foreach (var e in edges) GraphLines.Add($"{graph.Node(e.From).Caption} {e.Kind} {graph.Node(e.To).Caption}");
        GraphWidth = picture.Width + 120;
        GraphHeight = picture.Height + 40;
        GraphSummary = $"{nodes.Count} ticket(s) within {GraphHops} hop(s) of {graph.Node(focus).Caption}, {edges.Count} link(s). " +
                       "Border colour is the repo, size the graph weight; requires is a solid arrow, part-of a thick tree line (parent above), related dashed, other kinds dotted.";
    }
}
