namespace MindAttic.Tickets.Repos;

/// <summary>Why a candidate can't simply be deleted.</summary>
public enum BlockerKind
{
    /// <summary>Uncommitted, unpushed or stashed work, or local-only ignored files. The user may override this.</summary>
    LocalWork,
    /// <summary>Git couldn't read the repo.</summary>
    GitError,
    /// <summary>No GitHub copy to re-clone from. Never overridable.</summary>
    NoGitHubCopy,
    /// <summary>A kept repo reaches into this folder by path. Never overridable.</summary>
    Dependent,
}

/// <summary>One reason a candidate is blocked.</summary>
public sealed record PruneBlocker(BlockerKind Kind, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A repo that no longer belongs in the workspace, and anything stopping its deletion.</summary>
public sealed record PruneCandidate(RepoStatus Status, string Reason, IReadOnlyList<PruneBlocker> Blockers)
{
    public bool Safe => Blockers.Count == 0;

    /// <summary>
    /// True when every blocker is local work the user can knowingly give up. A missing GitHub copy, a
    /// git error or a kept dependent can never be overridden.
    /// </summary>
    public bool Overridable => Blockers.Count > 0 && Blockers.All(b => b.Kind == BlockerKind.LocalWork);
}

/// <summary>
/// Decides which local folders to remove: a repo goes when GitHub says it's archived or it lacks
/// the <c>wip</c> topic. It's only deleted when nothing would be lost — no uncommitted,
/// unpushed or stashed work, no local-only ignored files, no other kept repo reaching into it by
/// path, and a confirmed GitHub copy to restore from.
/// </summary>
public static class PrunePlanner
{
    public static IReadOnlyList<PruneCandidate> Plan(
        IReadOnlyList<RepoStatus> statuses,
        IReadOnlyDictionary<string, GitHubRepoInfo> github,
        IReadOnlyList<FolderDependency> dependencies,
        string topic = GitHubRepoIndex.WorkInProgressTopic)
    {
        var candidates = new List<(RepoStatus Status, string Reason, List<PruneBlocker> Blockers)>();
        foreach (var s in statuses)
        {
            var blockers = new List<PruneBlocker>();
            string reason;
            if (s.GitHubSlug is null)
            {
                reason = s.OriginUrl is null ? "no origin remote" : $"origin isn't on GitHub ({s.OriginUrl})";
                blockers.Add(new(BlockerKind.NoGitHubCopy, "no GitHub copy to restore from"));
            }
            else if (!github.TryGetValue(s.GitHubSlug, out var info))
            {
                reason = $"origin {s.GitHubSlug} not found on GitHub";
                blockers.Add(new(BlockerKind.NoGitHubCopy, "no GitHub copy to restore from"));
            }
            else if (info.Archived) reason = "archived on GitHub";
            else if (!info.HasTopic(topic)) reason = $"not tagged '{topic}'";
            else continue;

            if (s.Error is not null) blockers.Add(new(BlockerKind.GitError, $"git error: {s.Error}"));
            if (s.Uncommitted > 0) blockers.Add(new(BlockerKind.LocalWork, $"{s.Uncommitted} uncommitted change(s)"));
            if (s.Unpushed > 0) blockers.Add(new(BlockerKind.LocalWork, $"{s.Unpushed} unpushed commit(s)"));
            if (s.Stashes > 0) blockers.Add(new(BlockerKind.LocalWork, $"{s.Stashes} stash(es)"));
            if (s.LocalOnlyIgnored.Count > 0)
                blockers.Add(new(BlockerKind.LocalWork, $"local-only ignored files: {string.Join(", ", s.LocalOnlyIgnored.Take(3))}{(s.LocalOnlyIgnored.Count > 3 ? $" (+{s.LocalOnlyIgnored.Count - 3} more)" : "")}"));
            candidates.Add((s, reason, blockers));
        }

        // A kept repo depending on a candidate blocks the candidate. A candidate depending on another candidate
        // doesn't, but only once that other candidate is actually going too: one blocked by its own local work,
        // a git error, a missing GitHub copy, or (transitively) by this same rule, is staying on disk just as a
        // kept repo would, so anything depending on it must be blocked the same way. Iterate to a fixed point so
        // a chain of candidates depending on each other resolves correctly however deep it goes.
        bool changed;
        do
        {
            changed = false;
            var leaving = candidates.Where(c => c.Blockers.Count == 0).Select(c => c.Status.Repo.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
            {
                if (!leaving.Contains(c.Status.Repo.Name)) continue; // already blocked; no need to re-derive
                foreach (var d in dependencies.Where(d => d.ToRepo.Equals(c.Status.Repo.Name, StringComparison.OrdinalIgnoreCase) && !leaving.Contains(d.FromRepo)))
                {
                    c.Blockers.Add(new(BlockerKind.Dependent, $"{d.FromRepo} depends on it ({d.File} → {d.RawPath})"));
                    changed = true;
                }
            }
        } while (changed);

        return [.. candidates.Select(c => new PruneCandidate(c.Status, c.Reason, c.Blockers))];
    }

    /// <summary>
    /// Deletes a repo folder. Git marks pack and object files read-only on Windows, which makes a
    /// plain recursive delete fail part-way, so attributes are cleared first.
    /// </summary>
    public static void DeleteFolder(string path)
    {
        // Skip reparse points so a junction inside the repo is never followed out of it.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var f in Directory.EnumerateFiles(path, "*", options))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
