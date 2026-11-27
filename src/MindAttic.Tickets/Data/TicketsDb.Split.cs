using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>
/// Splitting a ticket into tasks (M.TIX-T363): each task becomes a child ticket part-of it, so the parent becomes a container.
/// </summary>
public sealed partial class TicketsDb
{
    /// <summary>
    /// Splits open ticket <paramref name="parentId"/> into one child per task: a new ticket in the parent's repo, of the parent's
    /// type, with the task's summary (and friendly name, when given), part-of the parent, level task, approved when the parent
    /// is, at the parent's priority (M.TIX-T387). With <paramref name="sequential"/> each child requires the one before it. A parent with no level, or level task,
    /// becomes level story. A closed parent is refused. Each child is added in its own transaction (<see cref="Add"/> takes
    /// the next number in one), then linked, levelled and approved in another. Returns the new tickets in task order.
    /// </summary>
    public IReadOnlyList<Ticket> Split(string parentId, IReadOnlyList<(string? Name, string Summary)> tasks, bool sequential)
    {
        if (tasks.Count == 0) throw new ArgumentException("Give at least one task.", nameof(tasks));
        if (tasks.Any(t => string.IsNullOrWhiteSpace(t.Summary)))
            throw new ArgumentException("Every task needs a summary.", nameof(tasks));
        foreach (var (name, _) in tasks) NormalizeName(name); // a bad name refuses the split before anything is written
        var (repo, parent) = Ticket(parentId) ?? throw new InvalidOperationException($"No ticket {parentId}.");
        if (parent.Status != TicketRules.Open) throw new InvalidOperationException($"{parent.Id} is {parent.Status}; only an open ticket can be split.");
        var (approval, priority) = QueueState(parent.Id);
        var approved = approval == Approved;

        if (Level(parent.Id) is null or LevelTask) SetLevel(parent.Id, LevelStory);
        var made = new List<Ticket>();
        foreach (var (name, summary) in tasks)
        {
            var child = Add(repo, parent.Type, TicketRules.Open, summary, "", name: string.IsNullOrWhiteSpace(name) ? null : name);
            var before = made.Count > 0 ? made[^1].Id : null;
            InWrite(() =>
            {
                AddLink(child.Id, parent.Id, PartOf, null);
                SetLevel(child.Id, LevelTask);
                if (approved) SetApprovalCore(child.Id, Approved);
                SetPriorityCore(child.Id, priority);
                if (sequential && before is not null) AddLink(child.Id, before, RequiresKind, null);
            });
            made.Add(child);
        }
        return made;
    }
}
