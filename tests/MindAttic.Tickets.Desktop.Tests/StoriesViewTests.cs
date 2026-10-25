using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// M.TIX-T82: the Stories view lists every repo's stories under their epics (filtered by repo, search and story status); the
/// editor saves a story's statement, status, acceptance and evidence (or reverts them), adds stories and epics, renames an
/// epic, and links the tickets that implement a story, undoably; a story ID clicked anywhere opens it there.
/// </summary>
public sealed class StoriesViewTests
{
    private string root = "", alpha = "";
    private MainViewModel model = null!;
    private readonly Queue<PromptAnswer?> answers = new();

    private sealed class Answers(Queue<PromptAnswer?> q) : IDialogs
    {
        public PromptAnswer? Ask(Prompt prompt) => q.Dequeue();
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => null;
    }

    [SetUp]
    public async Task SetUp()
    {
        answers.Clear();
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-stories-" + Guid.NewGuid().ToString("N"));
        alpha = Path.Combine(root, "Alpha");
        var beta = Path.Combine(root, "Beta");
        foreach (var dir in new[] { alpha, beta })
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            Directory.CreateDirectory(Path.Combine(dir, "docs"));
        }
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Epic A — Core {#core}\n- **AL-US-A1 ⬜** As a user, I can start. *Acceptance: it starts*\n" +
            "- **AL-US-A2 ✅** As a user, I can stop. *(verified by `StopTests`.)*\n\n" +
            "## Epic B — Extras {#extras}\n- **AL-US-B1 🟡** As a user, I can export.\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | build the start | — |\n" +
            "| AL-T2 | 2026-10-02 | feature | open | build the export | — |\n");
        File.WriteAllText(Path.Combine(beta, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Beta\ncode: BE\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Beta — User Stories\n\n" +
            "## Epic A — Other {#other}\n- **BE-US-A1 ⬜** As an admin, I can configure it.\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
        {
            db.Import([new Repo("Alpha", alpha), new Repo("Beta", beta)]);
            db.AddLink("AL-T1", "AL-US-A1", "implements");
        }
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), new Answers(answers)) { SelectedView = MainViewModel.StoriesView };
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

    private string[] Ids => [.. model.StoryRows.Select(r => r.Id)];

    private StoryRowViewModel Row(string id) => model.StoryRows.Single(r => r.Id == id);

    private async Task<StoryEditorViewModel> Open(string id)
    {
        var row = Row(id);
        model.SelectedStoryRow = row;
        await model.LoadStoryAsync(row.Repo, row.Id);
        return model.StoryEditor!;
    }

    private string Markdown => File.ReadAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"));

    [Test]
    public void Lists_every_repos_stories_in_epic_order_with_their_epic_and_implementers()
    {
        Assert.That(Ids, Is.EqualTo(new[] { "AL-US-A1", "AL-US-A2", "AL-US-B1", "BE-US-A1" }));
        Assert.That(Row("AL-US-B1").Group, Is.EqualTo(new EpicGroup("Alpha", "B", "Extras", Row("AL-US-B1").Group.Id)));
        Assert.That(Row("AL-US-B1").Group.Heading, Does.Contain("Epic B").And.Contain("Extras"));
        Assert.That(Row("AL-US-A1").ImplementersText, Is.EqualTo("Implemented by AL-T1"));
        Assert.That(Row("AL-US-A2").ImplementersText, Is.EqualTo("No implementing tickets"));
        Assert.That(Row("AL-US-A1").Statement, Is.EqualTo("As a user, I can start."), "the list shows the statement without its acceptance");
        Assert.That(model.StatusMessage, Does.StartWith("Stories: 4 of 4"));
        Assert.That(model.ShowsTicketFilters, Is.False);
    }

    [Test]
    public void Repo_status_and_search_narrow_the_list()
    {
        model.RepoFilter = "Beta";
        Assert.That(Ids, Is.EqualTo(new[] { "BE-US-A1" }));

        model.RepoFilter = MainViewModel.AllRepos;
        model.StoryStatusFilter = "done";
        Assert.That(Ids, Is.EqualTo(new[] { "AL-US-A2" }));

        model.StoryStatusFilter = MainViewModel.AnyStatus;
        model.Search = "extras";
        Assert.That(Ids, Is.EqualTo(new[] { "AL-US-B1" }), "an epic's title matches its stories");
        model.Search = "AL-T1";
        Assert.That(Ids, Is.EqualTo(new[] { "AL-US-A1" }), "an implementing ticket matches its story");
    }

    [Test]
    public async Task Saving_writes_the_statement_status_acceptance_and_evidence_and_the_markdown_follows()
    {
        var ed = await Open("AL-US-A1");
        Assert.That((ed.Statement, ed.Acceptance, ed.Evidence, ed.Status), Is.EqualTo(("As a user, I can start.", "it starts", "", "planned")));
        Assert.That(model.ShowsStoryEditor, Is.True);
        Assert.That(model.SaveStoryCommand.CanExecute(null), Is.False, "nothing to save yet");

        ed.Statement = "As a user, I can start quickly.";
        ed.Status = "partial";
        ed.Acceptance = "it starts in a second";
        ed.Evidence = "verified by `StartTests`.";
        Assert.That(ed.IsDirty, Is.True);
        Assert.That(Row("AL-US-A1").IsEdited, Is.True, "the list marks unsaved edits");

        Assert.That(await model.SaveStoryAsync(), Is.True);

        Assert.That(ed.IsDirty, Is.False);
        Assert.That(Row("AL-US-A1").Statement, Is.EqualTo("As a user, I can start quickly."));
        Assert.That(Row("AL-US-A1").Status, Is.EqualTo("partial"));
        Assert.That(Markdown, Does.Contain("AL-US-A1").And.Contain("I can start quickly.").And.Contain("it starts in a second").And.Contain("StartTests"));
    }

    [Test]
    public async Task Revert_drops_the_edits()
    {
        var ed = await Open("AL-US-A1");
        ed.Statement = "Something else.";
        ed.Status = "done";

        model.RevertStory();

        Assert.That((ed.Statement, ed.Status, ed.IsDirty), Is.EqualTo(("As a user, I can start.", "planned", false)));
        Assert.That(Row("AL-US-A1").IsEdited, Is.False);
    }

    [Test]
    public async Task A_story_changed_elsewhere_since_it_was_opened_is_not_overwritten()
    {
        var ed = await Open("AL-US-A1");
        ed.Statement = "My edit.";
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
            db.UpdateStory("AL-US-A1", "Alpha", "Their edit.", "partial", null, null);

        Assert.That(await model.SaveStoryAsync(), Is.False);

        Assert.That(ed.Error, Does.Contain("changed elsewhere"));
        Assert.That(ed.Statement, Is.EqualTo("My edit."), "the edits are kept");
        Assert.That(new TicketStore(root).StoryIn("Alpha", "AL-US-A1")!.Story.Text, Does.StartWith("Their edit."));
    }

    [Test]
    public async Task Unsaved_edits_survive_showing_another_story()
    {
        var ed = await Open("AL-US-A1");
        ed.Statement = "Kept while away.";

        await Open("AL-US-A2");
        var back = await Open("AL-US-A1");

        Assert.That(back.Statement, Is.EqualTo("Kept while away."));
        Assert.That(back.IsDirty, Is.True);
    }

    [Test]
    public async Task An_implementing_ticket_is_linked_and_unlinked_and_Ctrl_Z_takes_it_back()
    {
        var ed = await Open("AL-US-B1");
        answers.Enqueue(new PromptAnswer(null, "al-t2"));

        await model.AddImplementerAsync();

        Assert.That(model.StoryEditor!.Implementers.Select(i => i.TicketId), Is.EqualTo(new[] { "AL-T2" }));
        Assert.That(Row("AL-US-B1").ImplementersText, Is.EqualTo("Implemented by AL-T2"));

        await model.UndoAsync();
        await model.LoadStoryAsync(ed.Repo, ed.Id);
        Assert.That(model.StoryEditor!.Implementers, Is.Empty, "Ctrl+Z took the link back");

        var a1 = await Open("AL-US-A1");
        await model.RemoveImplementerAsync(a1.Implementers.Single());
        Assert.That(model.StoryEditor!.Implementers, Is.Empty);
        Assert.That(Row("AL-US-A1").ImplementersText, Is.EqualTo("No implementing tickets"));
    }

    [Test]
    public async Task Linking_something_that_is_not_a_ticket_is_refused_in_words()
    {
        var ed = await Open("AL-US-B1");
        answers.Enqueue(new PromptAnswer(null, "AL-T99"));

        await model.AddImplementerAsync();

        Assert.That(ed.Error, Is.EqualTo("AL-T99 isn't a ticket."));
        Assert.That(ed.Implementers, Is.Empty);
    }

    [Test]
    public async Task A_new_story_goes_under_the_epic_picked_and_opens_in_the_editor()
    {
        var epicB = Row("AL-US-B1").Group;
        answers.Enqueue(new PromptAnswer("B — Extras", "As a user, I can import."));

        await model.NewStoryAsync(epicB);

        var added = model.StoryRows.Single(r => r.Statement == "As a user, I can import.");
        Assert.That(added.Group.Key, Is.EqualTo("B"));
        Assert.That(added.Status, Is.EqualTo("planned"));
        Assert.That(model.SelectedStoryRow, Is.SameAs(added));
        Assert.That(Markdown, Does.Contain(added.Id).And.Contain("I can import."));
    }

    [Test]
    public async Task A_new_epic_is_added_and_an_epic_is_renamed()
    {
        model.RepoFilter = "Alpha";
        answers.Enqueue(new PromptAnswer(null, "c"));
        answers.Enqueue(new PromptAnswer(null, "Reports"));

        await model.NewEpicAsync();

        Assert.That(Markdown, Does.Contain("Epic C").And.Contain("Reports"));

        answers.Enqueue(new PromptAnswer(null, "Core features"));
        await model.RenameEpicAsync(Row("AL-US-A1").Group);

        Assert.That(Row("AL-US-A1").Group.Title, Is.EqualTo("Core features"));
        Assert.That(Row("AL-US-A1").Id, Is.EqualTo("AL-US-A1"), "its stories keep their IDs");
        Assert.That(Markdown, Does.Contain("Core features"));
    }

    [Test]
    public async Task A_story_id_clicked_anywhere_opens_it_in_the_Stories_view_with_filters_that_hid_it_cleared()
    {
        model.SelectedView = MainViewModel.QueueView;
        model.Search = "nothing matches this";

        model.GoTo("al-us-b1");

        Assert.That(model.SelectedView, Is.EqualTo(MainViewModel.StoriesView));
        Assert.That(model.Search, Is.Empty);
        Assert.That(model.SelectedStoryRow?.Id, Is.EqualTo("AL-US-B1"));
        Assert.That(() => model.StoryEditor?.Id, Is.EqualTo("AL-US-B1").After(5000, 20));
    }

    [Test]
    public async Task The_editor_takes_the_detail_panes_place_only_in_the_Stories_view()
    {
        await Open("AL-US-A1");
        await model.OpenTicketAsync("AL-T1");

        Assert.That(model.IsDetailOpen, Is.True);
        model.SelectedView = MainViewModel.StoriesView;
        Assert.That((model.ShowsStoryEditor, model.ShowsDetailPane), Is.EqualTo((true, false)));
        model.SelectedView = MainViewModel.QueueView;
        Assert.That((model.ShowsStoryEditor, model.ShowsDetailPane), Is.EqualTo((false, true)));
    }
}
