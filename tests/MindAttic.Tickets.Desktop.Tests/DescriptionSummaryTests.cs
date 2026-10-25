using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>M.TIX-T73: the detail pane shows a ticket's markdown description apart from its summary, edits it, and rewords the
/// summary as a recorded correction with a reason; a new ticket can be filed with a description.</summary>
public sealed class DescriptionSummaryTests
{
    private string root = "";
    private MainViewModel model = null!;
    private readonly Queue<PromptAnswer?> answers = new();
    private readonly List<Prompt> asked = [];
    private NewTicket? newTicket;

    private sealed class Answers(DescriptionSummaryTests t) : IDialogs
    {
        public PromptAnswer? Ask(Prompt prompt)
        {
            t.asked.Add(prompt);
            return t.answers.Dequeue();
        }

        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => t.newTicket;
    }

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-desc-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | stale wording | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root))) db.Import([new Repo("Alpha", alpha)]);
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), new Answers(this));
        await model.RefreshAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        for (var attempt = 1; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(50); }
        }
    }

    [Test]
    public async Task The_description_is_shown_and_edited_apart_from_the_summary()
    {
        Assert.That(await model.OpenTicketAsync("AL-T1"), Is.True);
        Assert.That((model.Detail!.HasDescription, model.Detail.Description), Is.EqualTo((false, "")));

        answers.Enqueue(new PromptAnswer(null, "## Why\n\n- it matters\n"));
        model.EditDescriptionCommand.Execute(null);
        await WaitFor(() => model.Detail?.HasDescription == true);
        Assert.That(asked[^1].Multiline, Is.True, "a description is several lines");
        Assert.That(model.Detail!.Description, Is.EqualTo("## Why\n\n- it matters"));
        Assert.That(model.Detail.Summary, Is.EqualTo("stale wording"));
        Assert.That(model.Detail.History.Select(h => h.Text), Has.Some.EqualTo("Description added"));

        answers.Enqueue(new PromptAnswer(null, ""));
        model.EditDescriptionCommand.Execute(null);
        await WaitFor(() => model.Detail?.HasDescription == false);
        Assert.That(asked[^1].Initial, Is.EqualTo("## Why\n\n- it matters"), "the editor starts on the current description");
        Assert.That(model.Detail!.History.Select(h => h.Text), Has.Some.EqualTo("Description removed"));
    }

    [Test]
    public async Task Rewording_the_summary_asks_why_and_is_recorded()
    {
        await model.OpenTicketAsync("AL-T1");
        answers.Enqueue(new PromptAnswer(null, "fresh wording"));
        answers.Enqueue(new PromptAnswer(null, "pre-rename wording"));
        model.CorrectSummaryCommand.Execute(null);
        await WaitFor(() => model.Detail?.Summary == "fresh wording");
        Assert.That(model.Detail!.History.Select(h => h.Text),
            Has.Some.EqualTo("summary: stale wording → fresh wording (corrected: pre-rename wording)"));

        answers.Enqueue(new PromptAnswer(null, "fresh wording"));
        model.CorrectSummaryCommand.Execute(null);
        Assert.That(answers, Is.Empty, "an unchanged wording asks no reason");
    }

    [Test]
    public async Task A_new_ticket_can_be_filed_with_a_description()
    {
        newTicket = new NewTicket("Alpha", "feature", "with a body", "", "Body text.");
        await model.NewTicketAsync();
        await WaitFor(() => model.Detail?.Id == "AL-T2");
        Assert.That(model.Detail!.Description, Is.EqualTo("Body text."));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.That(condition(), Is.True, "timed out");
    }
}
