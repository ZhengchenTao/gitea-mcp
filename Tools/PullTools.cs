using GiteaMcp.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace GiteaMcp.Tools;

/// <summary>Pull Request Tool：list_pulls / read_pull</summary>
[McpServerToolType]
public class PullTools(
    GiteaApiClient gitea,
    GiteaRepoFilter filter)
{
    [McpServerTool]
    [Description(
        "List pull requests in a Gitea repository. " +
        "state: 'open' (default), 'closed', or 'all'. " +
        "Returns: PR number, title, state, head/base branches, merged status, labels, and URL. " +
        "Use read_pull to get the full body, review comments, and changed files list.")]
    public async Task<object> list_pulls(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Filter by state: 'open', 'closed', or 'all'. Default 'open'.")] string? state = null,
        [Description("Max PRs to return. Default 30.")] int? limit = null,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        var st = state ?? "open";
        var lim = Math.Min(limit ?? 30, 50);
        var pulls = await gitea.GetPullsAsync(owner, repo, st, lim, ct);

        return pulls.Select(p => new
        {
            number = p.Number,
            title = p.Title,
            state = p.State,
            html_url = p.HtmlUrl,
            author = p.User?.Login,
            head = p.Head?.Ref,
            base_branch = p.Base?.Ref,
            merged = p.Merged,
            labels = p.Labels?.Select(l => l.Name).ToList() ?? [],
            created_at = p.CreatedAt,
            updated_at = p.UpdatedAt,
            merged_at = p.MergedAt,
        }).ToList();
    }

    [McpServerTool]
    [Description(
        "Get full details of a specific pull request: body, review comments, and list of changed files. " +
        "Changed files include filename, status (added/modified/removed), and line counts. " +
        "Use list_pulls first to find the PR number.")]
    public async Task<object> read_pull(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number (integer).")] int number,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        // 并行拉取 PR 主体、评论、变更文件
        var pullTask = gitea.GetPullAsync(owner, repo, number, ct);
        var commentsTask = gitea.GetPullCommentsAsync(owner, repo, number, ct);
        var filesTask = gitea.GetPullFilesAsync(owner, repo, number, ct);

        await Task.WhenAll(pullTask, commentsTask, filesTask);

        var pull = await pullTask;
        var comments = await commentsTask;
        var files = await filesTask;

        return new
        {
            number = pull.Number,
            title = pull.Title,
            body = pull.Body,
            state = pull.State,
            html_url = pull.HtmlUrl,
            author = pull.User?.Login,
            head = pull.Head?.Ref,
            head_sha = pull.Head?.Sha,
            base_branch = pull.Base?.Ref,
            merged = pull.Merged,
            mergeable = pull.Mergeable,
            labels = pull.Labels?.Select(l => l.Name).ToList() ?? [],
            created_at = pull.CreatedAt,
            updated_at = pull.UpdatedAt,
            closed_at = pull.ClosedAt,
            merged_at = pull.MergedAt,
            comments = comments.Select(c => new
            {
                id = c.Id,
                author = c.User?.Login,
                body = c.Body,
                created_at = c.CreatedAt,
            }).ToList(),
            changed_files = files.Select(f => new
            {
                filename = f.Filename,
                status = f.Status,
                additions = f.Additions,
                deletions = f.Deletions,
                changes = f.Changes,
            }).ToList(),
        };
    }
}
