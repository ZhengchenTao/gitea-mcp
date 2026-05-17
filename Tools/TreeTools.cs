using GiteaMcp.Config;
using GiteaMcp.Services;
using GiteaMcp.Services.Models;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace GiteaMcp.Tools;

/// <summary>文件树与文件内容 Tool：list_tree / read_file</summary>
[McpServerToolType]
public class TreeTools(
    GiteaApiClient gitea,
    GiteaRepoFilter filter,
    IOptions<GiteaOptions> opts)
{
    private readonly GiteaOptions _opts = opts.Value;

    [McpServerTool]
    [Description(
        "List the file tree of a Gitea repository at a given ref (branch/tag/SHA). " +
        "When recursive=false (default), returns only top-level entries. " +
        "When recursive=true, returns all files up to max_entries=500 — use this to map repo structure. " +
        "Returns: path, type ('blob'=file, 'tree'=directory), size (bytes), sha. " +
        "For very large repos (>500 files), truncated=true will be set; narrow down by adjusting paths manually.")]
    public async Task<object> list_tree(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Branch name, tag, or commit SHA. Defaults to the repo's default branch.")] string? @ref = null,
        [Description("Recursively include all files in subdirectories. Default false.")] bool recursive = false,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        // ref 未提供时，先拿 default_branch
        if (string.IsNullOrWhiteSpace(@ref))
        {
            var repoMeta = await gitea.GetRepoAsync(owner, repo, ct);
            @ref = repoMeta.DefaultBranch;
        }

        var tree = await gitea.GetTreeAsync(owner, repo, @ref, recursive, ct);

        const int MaxEntries = 500;
        var entries = tree.Tree;
        bool truncated = tree.Truncated || entries.Count > MaxEntries;
        if (entries.Count > MaxEntries)
            entries = entries.Take(MaxEntries).ToList();

        return new
        {
            owner,
            repo,
            @ref,
            truncated,
            entry_count = entries.Count,
            entries = entries.Select(e => new
            {
                path = e.Path,
                type = e.Type,
                size = e.Size,
                sha = e.Sha,
            }),
        };
    }

    [McpServerTool]
    [Description(
        "Read the raw text content of a file from a Gitea repository. " +
        "Returns the file as UTF-8 text. Binary files will appear garbled — check file extension first. " +
        "Truncated to max_bytes (default 1MB); when truncated=true, the content is cut off. " +
        "Use list_tree first to discover file paths.")]
    public async Task<object> read_file(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("File path relative to repo root, e.g. 'src/Main.cs' or 'README.md'.")] string path,
        [Description("Branch, tag, or SHA. Defaults to repo's default branch.")] string? @ref = null,
        [Description("Max bytes to read. Default 1048576 (1MB). Reduce for large binary-adjacent files.")] int? max_bytes = null,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        if (string.IsNullOrWhiteSpace(@ref))
        {
            var repoMeta = await gitea.GetRepoAsync(owner, repo, ct);
            @ref = repoMeta.DefaultBranch;
        }

        var maxB = Math.Min(max_bytes ?? _opts.MaxFileBytes, _opts.MaxFileBytes);
        var (content, truncated) = await gitea.GetRawFileAsync(owner, repo, @ref, path, maxB, ct);

        return new
        {
            owner,
            repo,
            @ref,
            path,
            truncated,
            content,
        };
    }
}
