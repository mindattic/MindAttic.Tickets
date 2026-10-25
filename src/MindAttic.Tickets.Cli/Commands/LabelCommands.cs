using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

// Ticket labels and components (M.TIX-T74): `tickets ticket label add|remove|list`. A component is a 'component:' label.

public sealed class LabelChangeSettings : WorkspaceSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("Ticket ID, e.g. M.TIX-T74.")]
    public string Id { get; init; } = "";

    [CommandArgument(1, "<LABELS>")]
    [Description("One or more labels: a letter or digit first, then letters, digits and - _ . : / (lower-cased). A component is 'component:<name>'.")]
    public string[] Labels { get; init; } = [];
}

/// <summary><c>tickets ticket label add &lt;ID&gt; &lt;LABEL&gt;...</c>: label a ticket.</summary>
public sealed class TicketLabelAddCommand : Command<LabelChangeSettings>
{
    public override int Execute(CommandContext context, LabelChangeSettings settings)
    {
        using var db = Db.Open(settings);
        var now = db.AddLabels(settings.Id, settings.Labels);
        Console.WriteLine($"{db.Ticket(settings.Id)!.Value.Ticket.Id} labels: {string.Join(", ", now)}");
        return 0;
    }
}

/// <summary><c>tickets ticket label remove &lt;ID&gt; &lt;LABEL&gt;...</c>: take labels off a ticket.</summary>
public sealed class TicketLabelRemoveCommand : Command<LabelChangeSettings>
{
    public override int Execute(CommandContext context, LabelChangeSettings settings)
    {
        using var db = Db.Open(settings);
        var now = db.RemoveLabels(settings.Id, settings.Labels);
        Console.WriteLine($"{db.Ticket(settings.Id)!.Value.Ticket.Id} labels: {(now.Count == 0 ? "none" : string.Join(", ", now))}");
        return 0;
    }
}

/// <summary><c>tickets ticket label list [ID]</c>: a ticket's labels, or every label in use with its ticket count.</summary>
public sealed class TicketLabelListCommand : Command<TicketLabelListCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "[ID]")]
        [Description("A ticket: its labels. Without it, every label in use and how many tickets carry it.")]
        public string? Id { get; init; }

        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        if (settings.Id is { Length: > 0 } id)
        {
            var (_, t) = db.Ticket(id) ?? throw new KeyNotFoundException($"No ticket {id}.");
            var labels = db.Labels(t.Id);
            Console.WriteLine(settings.Json ? JsonSerializer.Serialize(labels) : $"{t.Id} labels: {(labels.Count == 0 ? "none" : string.Join(", ", labels))}");
            return 0;
        }
        var counts = db.LabelCounts();
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(counts.Select(c => new { label = c.Label, tickets = c.Tickets }), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (counts.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No ticket has a label yet; add one with 'tickets ticket label add <ID> <LABEL>'.[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Label", "Tickets");
        foreach (var (label, n) in counts) table.AddRow(Output.Esc(label), n.ToString(CultureInfo.InvariantCulture));
        AnsiConsole.Write(table);
        return 0;
    }
}
