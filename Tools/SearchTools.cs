using GiteaMcp.Services;
using GiteaMcp.Services.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace GiteaMcp.Tools;

/// <summary>代码搜索 Tool：search_code</summary>
[McpServerToolType]
public class SearchTools(
    GiteaApiClient gitea,
    GiteaRepoFilter filter)
{
    [McpServerTool]
    [Description(
        "Search code across Gitea repositories using Gitea's built-in code indexer. " +
        "Requires Gitea to have REPO_INDEXER_ENABLED=true in app.ini. " +
        "If the indexer is not enabled, returns an explanatory error instead of silently returning empty. " +
        "Scope: when owner+repo are provided, searches only that repo; otherwise searches all accessible repos. " +
        "Returns: owner, repo, file path, line number, and a preview snippet of the matching line. " +
        "Limit: max 50 results. For large codebases, narrow with owner or repo.")]
    public async Task<object> search_code(
        [Description("Search query string. Supports keywords; no regex.")] string query,
        [Description("Restrict search to this owner (user login or org name). Optional.")] string? owner = null,
        [Description("Restrict search to this repo name (requires owner). Optional.")] string? repo = null,
        [Description("Max number of results. Default 50.")] int? limit = null,
        CancellationToken ct = default)
    {
        var lim = Math.Min(limit ?? 50, 50);

        // 检查是否需要过滤黑名单
        if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo)
            && filter.IsBlocked($"{owner}/{repo}"))
        {
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");
        }

        // 调 Gitea code search 端点（仅在 [indexer] REPO_INDEXER_ENABLED=true 时可用）。
        // indexer 未启用 → API 返回 404，GiteaApiClient.EnsureSuccessAsync 抛 KeyNotFoundException，
        // 这里捕获后返回结构化降级提示，避免 swallow 到空数组让 Claude 误以为"搜不到"。
        try
        {
            var codeResult = await gitea.SearchCodeAsync(query, owner, repo, lim, ct);

            var hits = codeResult.Data
                .Where(d => d.Repo == null || !filter.IsBlocked(d.Repo.FullName))
                .Take(lim)
                .Select(d => (object)new
                {
                    owner = d.Repo?.Owner?.Login ?? "",
                    repo = d.Repo?.Name ?? "",
                    path = d.Filename,
                    // Gitea code search 不返回精确行号，前端可在 preview 里自行定位
                    line = 0,
                    preview = d.Content?.Length > 200 ? d.Content[..200] + "..." : d.Content ?? "",
                })
                .ToList();

            return new { ok = true, results = hits };
        }
        catch (KeyNotFoundException)
        {
            // indexer 未启用 → 404；返回结构化提示，不要 swallow 成空 results
            return new
            {
                ok = false,
                error = "indexer_disabled",
                notice = "Gitea code search endpoint returned 404. " +
                         "Enable the code indexer by setting [indexer] REPO_INDEXER_ENABLED=true in app.ini and restart Gitea. " +
                         "Workaround: use list_tree + read_file to navigate files manually.",
                results = Array.Empty<object>(),
            };
        }
    }
}
