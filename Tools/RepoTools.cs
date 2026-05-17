using GiteaMcp.Config;
using GiteaMcp.Services;
using GiteaMcp.Services.Models;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace GiteaMcp.Tools;

/// <summary>仓库级别 Tool：list_repos / read_repo</summary>
[McpServerToolType]
public class RepoTools(
    GiteaApiClient gitea,
    GiteaRepoFilter filter,
    IOptions<GiteaOptions> opts)
{
    private readonly GiteaOptions _opts = opts.Value;

    [McpServerTool]
    [Description(
        "List Gitea repositories accessible to the admin token. " +
        "When owner is omitted, returns ALL repos across personal account and all orgs (up to limit). " +
        "Use visibility='private' to show only private repos, 'public' for public-only. " +
        "Returns: owner, repo name, description, default_branch, private flag, size (KB), updated_at, html_url. " +
        "Tip: call this first to discover available repos before using read_repo or read_file.")]
    public async Task<List<object>> list_repos(
        [Description("Filter by owner login (user or org name). Omit to list everything.")] string? owner = null,
        [Description("Visibility filter: 'public', 'private', or 'all' (default 'all').")] string? visibility = null,
        [Description("Max number of repos to return. Default 50, max 50.")] int? limit = null,
        CancellationToken ct = default)
    {
        var vis = visibility ?? "all";
        var lim = Math.Min(limit ?? _opts.DefaultLimit, 50);

        var repos = await gitea.SearchReposAsync(owner, vis, lim, ct);
        repos = filter.Filter(repos);

        return repos.Select(r => (object)new
        {
            owner = r.Owner?.Login ?? r.FullName.Split('/').FirstOrDefault() ?? "",
            repo = r.Name,
            full_name = r.FullName,
            description = r.Description,
            default_branch = r.DefaultBranch,
            @private = r.Private,
            mirror = r.Mirror,
            archived = r.Archived,
            size_kb = r.Size,
            updated_at = r.UpdatedAt,
            html_url = r.HtmlUrl,
        }).ToList();
    }

    [McpServerTool]
    [Description(
        "Get detailed metadata for a single Gitea repository: topics, homepage, default branch, " +
        "stars, forks, archived status, mirror flag, size, and description. " +
        "Use this after list_repos to inspect a specific repo before reading files or commits.")]
    public async Task<object> read_repo(
        [Description("Repository owner (user login or org name).")] string owner,
        [Description("Repository name (without owner prefix).")] string repo,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        var r = await gitea.GetRepoAsync(owner, repo, ct);
        return new
        {
            owner = r.Owner?.Login ?? owner,
            repo = r.Name,
            full_name = r.FullName,
            description = r.Description,
            default_branch = r.DefaultBranch,
            @private = r.Private,
            fork = r.Fork,
            mirror = r.Mirror,
            archived = r.Archived,
            stars = r.StarsCount,
            forks = r.ForksCount,
            size_kb = r.Size,
            website = r.Website,
            topics = r.Topics ?? [],
            html_url = r.HtmlUrl,
            clone_url = r.CloneUrl,
            updated_at = r.UpdatedAt,
        };
    }
}
