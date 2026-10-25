using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>Ticket-to-commit links (M.TIX-T85): a ticket's commits are those whose subject names it, linked to GitHub.</summary>
public sealed class TicketCommitsTests
{
    private string dir = "";

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "tix-commits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Run("init", "-q", "-b", "main");
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    private void Run(params string[] args)
    {
        var r = Git.Run(dir, args);
        Assert.That(r.Ok, Is.True, r.Error);
    }

    private void Commit(string message) => Run("-c", "user.email=t@t", "-c", "user.name=Tess", "commit", "-q", "--allow-empty", "-m", message);

    [Test]
    public void Finds_the_commits_whose_subject_names_the_ticket_newest_first_with_GitHub_links()
    {
        Run("remote", "add", "origin", "git@github.com:MindAttic/Alpha.git");
        Commit("First pass (AA-T1)");
        Commit("Unrelated (AA-T10)");
        Commit("Mentions it only in the body (AA-T2)\n\nFollows up AA-T1.");
        Commit("Second pass (AA-T1)");

        var found = TicketCommits.Find("AA-T1", dir);

        Assert.That(found.Select(c => c.Subject), Is.EqualTo(new[] { "Second pass (AA-T1)", "First pass (AA-T1)" }),
            "AA-T10 isn't AA-T1, and a body mention isn't the ticket's commit");
        var head = Git.Run(dir, "rev-parse", "HEAD").Output.Trim();
        Assert.That(found[0].Hash, Is.EqualTo(head));
        Assert.That(head, Does.StartWith(found[0].ShortHash));
        Assert.That(found[0].Author, Is.EqualTo("Tess"));
        Assert.That(found[0].Url, Is.EqualTo($"https://github.com/mindattic/alpha/commit/{head}"));
    }

    [Test]
    public void Has_no_link_without_a_GitHub_origin_and_nothing_outside_a_repo()
    {
        Commit("Work (AA-T1)");
        Assert.That(TicketCommits.Find("AA-T1", dir).Single().Url, Is.Null);
        Assert.That(TicketCommits.Find("AA-T1", Path.Combine(dir, "missing")), Is.Empty);
    }

    [Test]
    public void Parse_keeps_each_commit_once()
    {
        const string log = "abc123\x1f" + "abc\x1f" + "2026-10-25 10:00\x1fTess\x1fWork (AA-T1)\x1e\n" +
                           "abc123\x1f" + "abc\x1f" + "2026-10-25 10:00\x1fTess\x1fWork (AA-T1)\x1e\n";
        Assert.That(TicketCommits.Parse(log, "AA-T1", "o/r").Single().Url, Is.EqualTo("https://github.com/o/r/commit/abc123"));
    }
}
