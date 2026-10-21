using MindAttic.Tickets.Stories;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

public sealed class StoryDocumentTests
{
    private const string Sample =
        """
        ---
        codex: 1
        project: Demo
        code: DM
        layer: stories
        status: living
        updated: 2026-10-12
        ---

        # Demo — User Stories
        > ✅ done (shipped & tested) · 🟡 partial · ⬜ planned. Every ✅ cites the test.

        ## Epic A — Basics {#epic-a}
        > Intro for the epic.

        - **DM-US-A1 ✅** As a user, I can do a thing. *(verified by `Does_a_thing`.)*
        - **DM-US-A2 🟡 (retired)** As a user, I can do another
          thing over two lines. *Acceptance: it works.*
          - **A2a ⬜** A nested step.
            still the nested step.

        ## Priority backlog
        1. **DM-US-A2 → ✅** finish it
           with a wrapped line.
        2. Something else (DM-US-A1).

        ## Battle core
        | ID | Story | § | Status | Evidence |
        |---|---|---|---|---|
        | US-001 | A table story | §3 | ✅ | `TableTest` |
        | US-002 | Another | | ⬜ | none |

        ## Tickets
        > legend

        | ID | Date | Type | Status | Summary | Ref |
        |---|---|---|---|---|---|
        | DM-T1 | 2026-10-01 | fix | open | a \| pipe | — |
        |  DM-T2 | 2026-10-02 | bugfix | closed | odd type, odd spacing | DM-T1 |
        """;

    [Test]
    public void Parses_epics_stories_backlog_table_stories_and_tickets_and_renders_back_byte_for_byte()
    {
        var crlf = Sample.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
        var bytes = StoryDocument.Encode(crlf.Replace("\r\n", "\n"), bom: true, crlf: true);
        var doc = StoryDocument.FromBytes(bytes);

        Assert.That(doc.RenderBytes(), Is.EqualTo(bytes), "BOM, CRLF and every line kept");
        Assert.That((doc.Code, doc.Title, doc.FrontMatter["updated"]), Is.EqualTo(("DM", "Demo — User Stories", "2026-10-12")));

        var epic = doc.Blocks.Single(b => b.Kind == BlockKind.Epic).Epic!;
        Assert.That(epic, Is.EqualTo(new Epic("A", "Basics", "epic-a")));

        var s = doc.Stories;
        Assert.That(s.Select(x => (x.Id, x.Status, x.Source)), Is.EqualTo(new[]
        {
            ("DM-US-A1", "done", "bullet"), ("DM-US-A2", "partial", "bullet"), ("A2a", "planned", "sub-bullet"),
            ("US-001", "done", "table"), ("US-002", "planned", "table"),
        }));
        Assert.That(s[0].Evidence, Is.EqualTo("verified by `Does_a_thing`."));
        Assert.That((s[1].Suffix, s[1].Acceptance, s[1].Epic), Is.EqualTo(("(retired)", "it works.", "A")));
        Assert.That(s[1].Text, Does.Contain("over two lines"));
        Assert.That(s[2].Parent, Is.EqualTo(1));
        Assert.That(s[2].Text, Does.Contain("still the nested step"));
        Assert.That((s[3].Section, s[3].SectionRef, s[3].Evidence), Is.EqualTo(("Battle core", "§3", "`TableTest`")));

        var backlog = doc.Blocks.Where(b => b.Kind == BlockKind.BacklogItem).Select(b => b.Backlog!).ToList();
        Assert.That(backlog.Select(b => b.StoryIds.Single()), Is.EqualTo(new[] { "DM-US-A2", "DM-US-A1" }));
        Assert.That(backlog[0].Text, Does.Contain("with a wrapped line"));

        Assert.That(doc.Tickets.Select(t => (t.Ticket.Id, t.Ticket.Summary)), Is.EqualTo(new[] { ("DM-T1", "a | pipe"), ("DM-T2", "odd type, odd spacing") }));
        Assert.That(doc.Tickets[1].Markdown, Does.StartWith("|  DM-T2"), "the exact row, odd spacing and all");
    }

    [Test]
    public void Every_file_in_the_real_workspace_renders_back_byte_for_byte()
    {
        var ws = new Workspace(Workspace.DefaultRoot);
        var files = ws.Repos().Select(r => r.StoriesPath).Where(File.Exists).ToList();
        if (files.Count == 0) Assert.Ignore("no workspace on this machine");

        var failed = files.Where(f => !StoryDocument.Load(f).RenderBytes().SequenceEqual(File.ReadAllBytes(f))).ToList();
        Assert.That(failed, Is.Empty);
    }
}
