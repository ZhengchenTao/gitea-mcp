using GiteaMcp.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace GiteaMcp.Tools;

/// <summary>Issue Tool：list_issues / read_issue</summary>
[McpServerToolType]
public class IssueTools(
    GiteaApiClient gitea,
    GiteaRepoFilter filter)
{
    [McpServerTool]
    [Description(
        "List issues in a Gitea repository. " +
        "state can be 'open' (default), 'closed', or 'all'. " +
        "Returns: issue number, title, state, labels, assignees, created_at, comment count, and URL. " +
        "Use read_issue to get the full body and comments of a specific issue.")]
    public async Task<object> list_issues(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Filter by state: 'open', 'closed', or 'all'. Default 'open'.")] string? state = null,
        [Description("Max issues to return. Default 30.")] int? limit = null,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        var st = state ?? "open";
        var lim = Math.Min(limit ?? 30, 50);
        var issues = await gitea.GetIssuesAsync(owner, repo, st, lim, ct);

        return issues.Select(i => new
        {
            number = i.Number,
            title = i.Title,
            state = i.State,
            html_url = i.HtmlUrl,
            author = i.User?.Login,
            labels = i.Labels?.Select(l => l.Name).ToList() ?? [],
            assignees = i.Assignees?.Select(a => a.Login).ToList() ?? [],
            comments = i.Comments,
            created_at = i.CreatedAt,
            updated_at = i.UpdatedAt,
            closed_at = i.ClosedAt,
        }).ToList();
    }

    [McpServerTool]
    [Description(
        "Get the full body and all comments of a specific Gitea issue. " +
        "Use list_issues first to find the issue number. " +
        "Returns: title, body (Markdown), state, labels, and all comment texts with authors and timestamps.")]
    public async Task<object> read_issue(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Issue number (integer, e.g. 42).")] int number,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        var issue = await gitea.GetIssueAsync(owner, repo, number, ct);
        var comments = await gitea.GetIssueCommentsAsync(owner, repo, number, ct);

        return new
        {
            number = issue.Number,
            title = issue.Title,
            body = issue.Body,
            state = issue.State,
            html_url = issue.HtmlUrl,
            author = issue.User?.Login,
            labels = issue.Labels?.Select(l => l.Name).ToList() ?? [],
            assignees = issue.Assignees?.Select(a => a.Login).ToList() ?? [],
            created_at = issue.CreatedAt,
            updated_at = issue.UpdatedAt,
            closed_at = issue.ClosedAt,
            comments = comments.Select(c => new
            {
                id = c.Id,
                author = c.User?.Login,
                body = c.Body,
                created_at = c.CreatedAt,
                updated_at = c.UpdatedAt,
            }).ToList(),
        };
    }
}
