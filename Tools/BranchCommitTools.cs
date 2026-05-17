using GiteaMcp.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace GiteaMcp.Tools;

/// <summary>分支与 commit Tool：list_branches / list_commits / read_commit</summary>
[McpServerToolType]
public class BranchCommitTools(
    GiteaApiClient gitea,
    GiteaRepoFilter filter)
{
    [McpServerTool]
    [Description(
        "List all branches in a Gitea repository with their latest commit info. " +
        "Returns: branch name, protected flag, last commit SHA, message, timestamp, and author. " +
        "Use this to discover available branches before calling list_commits or read_file with a specific ref.")]
    public async Task<object> list_branches(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        var branches = await gitea.GetBranchesAsync(owner, repo, ct);
        return branches.Select(b => new
        {
            name = b.Name,
            @protected = b.Protected,
            last_commit = b.Commit == null ? null : new
            {
                sha = b.Commit.Id,
                message = b.Commit.Message,
                timestamp = b.Commit.Timestamp,
                author = b.Commit.Author?.Name,
            },
        }).ToList();
    }

    [McpServerTool]
    [Description(
        "List recent commits in a Gitea repository. " +
        "Returns: SHA (short+full), commit message, author, and timestamp. " +
        "Use ref to specify a branch or tag; use since (ISO 8601 date) to filter by date. " +
        "Default returns up to 30 commits. Good for 'what changed recently' questions.")]
    public async Task<object> list_commits(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Branch, tag, or SHA. Defaults to the default branch.")] string? @ref = null,
        [Description("Only commits after this date (ISO 8601, e.g. '2026-04-01T00:00:00Z'). Optional.")] string? since = null,
        [Description("Max commits to return. Default 30.")] int? limit = null,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        var lim = Math.Min(limit ?? 30, 100);
        var commits = await gitea.GetCommitsAsync(owner, repo, @ref, since, lim, ct);

        return commits.Select(c => new
        {
            sha = c.Sha,
            sha_short = c.Sha.Length >= 8 ? c.Sha[..8] : c.Sha,
            message = c.Commit?.Message,
            author = c.Commit?.Author?.Name,
            author_email = c.Commit?.Author?.Email,
            timestamp = c.Commit?.Author?.Date ?? c.Created,
        }).ToList();
    }

    [McpServerTool]
    [Description(
        "Get full details of a single commit: message, author, stats, and per-file diff. " +
        "Files are limited to max_files=50; when truncated=true, some files are omitted. " +
        "Patch (diff text) is included per file — useful for code review or understanding specific changes. " +
        "Use list_commits first to obtain a SHA.")]
    public async Task<object> read_commit(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Full or abbreviated commit SHA.")] string sha,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        const int MaxFiles = 50;
        var commit = await gitea.GetCommitAsync(owner, repo, sha, ct);

        var files = commit.Files ?? [];
        bool truncated = files.Count > MaxFiles;
        if (truncated) files = files.Take(MaxFiles).ToList();

        return new
        {
            sha = commit.Sha,
            message = commit.Commit?.Message,
            author = commit.Commit?.Author?.Name,
            author_email = commit.Commit?.Author?.Email,
            authored_at = commit.Commit?.Author?.Date,
            committer = commit.Commit?.Committer?.Name,
            committed_at = commit.Commit?.Committer?.Date ?? commit.Created,
            stats = commit.Stats == null ? null : new
            {
                total = commit.Stats.Total,
                additions = commit.Stats.Additions,
                deletions = commit.Stats.Deletions,
            },
            files_truncated = truncated,
            files = files.Select(f => new
            {
                filename = f.Filename,
                status = f.Status,
                additions = f.Additions,
                deletions = f.Deletions,
                changes = f.Changes,
                patch = f.Patch,
            }).ToList(),
        };
    }
}
