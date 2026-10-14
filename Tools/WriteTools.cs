using GiteaMcp.Config;
using GiteaMcp.Services;
using GiteaMcp.Services.Models;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace GiteaMcp.Tools;

/// <summary>受 write:gitea、独立写 PAT 与 Gitea 账号权限约束的变更工具。</summary>
[McpServerToolType]
public sealed class WriteTools(
    GiteaApiClient gitea,
    GiteaRepoFilter filter,
    IOptions<GiteaOptions> options)
{
    private readonly GiteaOptions _options = options.Value;

    [McpServerTool]
    [Description(
        "Create a new branch from a branch, tag, or commit SHA. Requires write:gitea and write permission in Gitea. " +
        "If fromRef is omitted, the repository default branch is used. Gitea permissions and configured branch restrictions apply. " +
        "If the result is unknown after a timeout, call list_branches before retrying.")]
    public async Task<object> create_branch(
        [Description("Repository owner login, for example dev.")] string owner,
        [Description("Repository name, for example room-organizer.")] string repo,
        [Description("New working branch name, for example codex/update-room-yaml.")] string newBranch,
        [Description("Source branch, tag, or commit SHA. Defaults to the repository default branch.")] string? fromRef = null,
        CancellationToken ct = default)
    {
        return await ExposeWriteErrorAsync(async () =>
        {
            var branch = await gitea.CreateBranchAsync(owner, repo, newBranch, fromRef, ct);
            return new
            {
                name = branch.Name,
                sha = branch.Commit?.Id,
                @protected = branch.Protected,
            };
        });
    }

    [McpServerTool]
    [Description(
        "Create a new UTF-8 text file or update one file on a working branch. expectedHeadSha must be the current branch HEAD; expectedSha is also required when updating an existing file. " +
        "Use create_branch/list_branches for expectedHeadSha, list_tree for the file blob SHA, and read_file for content. The server uses Gitea's conditional branch-ref update to reject a stale HEAD. " +
        "Requires write:gitea, Gitea:WritePat, and write permission in Gitea. Direct commits to main/default branches are supported, subject to Gitea permissions and configured branch restrictions.")]
    public async Task<object> upsert_file(
        [Description("Repository owner login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Relative file path such as data/room.yaml. No absolute paths or traversal segments.")] string path,
        [Description("Complete UTF-8 text content of the new or updated file.")] string content,
        [Description("Existing working branch; required.")] string branch,
        [Description("Current HEAD commit SHA of the working branch. Obtain it from create_branch or list_branches.")] string expectedHeadSha,
        [Description("Current file blob SHA. Required for update and must be omitted for create.")] string? expectedSha = null,
        [Description("Commit message. Defaults to 'Update files via Gitea MCP'.")] string? message = null,
        CancellationToken ct = default)
    {
        return await ExposeWriteErrorAsync(async () =>
        {
            var result = await gitea.UpsertFileAsync(owner, repo, path, content, branch, expectedHeadSha, expectedSha, message, ct);
            return FormatCommit(result, [path]);
        });
    }

    [McpServerTool]
    [Description(
        "Delete one file from a working branch. expectedSha protects the file version and expectedHeadSha protects the branch HEAD. Both are mandatory. " +
        "Requires write:gitea and write permission in Gitea. Direct commits to main/default branches follow Gitea permissions and configured branch restrictions.")]
    public async Task<object> delete_file(
        [Description("Repository owner login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Relative repository path of the file to delete.")] string path,
        [Description("Current file blob SHA; required.")] string expectedSha,
        [Description("Existing working branch; required.")] string branch,
        [Description("Current HEAD commit SHA of the working branch. Obtain it from create_branch or list_branches.")] string expectedHeadSha,
        [Description("Commit message. Defaults to 'Update files via Gitea MCP'.")] string? message = null,
        CancellationToken ct = default)
    {
        return await ExposeWriteErrorAsync(async () =>
        {
            var result = await gitea.DeleteFileAsync(owner, repo, path, expectedSha, branch, expectedHeadSha, message, ct);
            return FormatCommit(result, [path]);
        });
    }

    [McpServerTool]
    [Description(
        "Commit multiple file creations, updates, and deletions together as one Gitea commit. " +
        "Each changes item has operation=create|update|delete, path, optional content, and expectedSha (required for update/delete). expectedHeadSha is the current working-branch tip. " +
        "The server stages the batch on a unique temporary ref, then conditionally moves the target branch using Gitea's old_commit_id compare-and-swap with force=false. A stale HEAD leaves the target branch unchanged; cleanup failures are reported as cleanup_warning. " +
        "If the branch update outcome is unknown, inspect list_branches/get_diff before retrying; do not resend the batch blindly. " +
        "Requires write:gitea and write permission in Gitea. Supports main/default branches; Gitea enforces its own branch protection and push permissions.")]
    public async Task<object> commit_files(
        [Description("Repository owner login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Existing working branch to receive the commit; required.")] string branch,
        [Description("Current HEAD commit SHA of the working branch. Obtain it from create_branch or list_branches; Gitea's conditional branch update rejects stale values atomically.")] string expectedHeadSha,
        [Description("Commit message for the complete batch.")] string message,
        [Description("File operations. For create: content required, expectedSha omitted. For update: content and current expectedSha required. For delete: content omitted and expectedSha required.")] IReadOnlyList<GiteaWriteFileChange> changes,
        CancellationToken ct = default)
    {
        return await ExposeWriteErrorAsync(async () =>
        {
            var result = await gitea.CommitFilesAsync(owner, repo, branch, expectedHeadSha, message, changes, ct);
            return FormatCommit(result, changes.Select(c => c.Path).ToArray());
        });
    }

    [McpServerTool]
    [Description(
        "Create a pull request from a working branch to a target branch, with a title and Markdown description. " +
        "Returns the PR number and URL. Requires write:gitea and write permission in Gitea. " +
        "If the result is unknown after a timeout, check for an existing PR with the same head/base before retrying.")]
    public async Task<object> create_pull_request(
        [Description("Repository owner login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Source working branch.")] string head,
        [Description("Target branch, commonly the repository default branch.")] string baseBranch,
        [Description("Pull request title.")] string title,
        [Description("Markdown pull request description.")] string body,
        CancellationToken ct = default)
    {
        return await ExposeWriteErrorAsync(async () =>
        {
            var pull = await gitea.CreatePullRequestAsync(owner, repo, head, baseBranch, title, body, ct);
            return new
            {
                number = pull.Number,
                title = pull.Title,
                state = pull.State,
                head = pull.Head?.Ref,
                base_branch = pull.Base?.Ref,
                html_url = pull.HtmlUrl,
            };
        });
    }

    [McpServerTool]
    [Description(
        "Merge an existing PR after reviewing read_pull/get_diff. Requires write:gitea and write permission in Gitea. " +
        "expectedHeadSha must match the reviewed PR head. Failed/pending CI is rejected; Gitea enforces required reviews and branch protection. " +
        "Never force-merges or deletes the source branch. On an unknown outcome, read_pull before retrying.")]
    public Task<object> merge_pull_request(
        [Description("Repository owner login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("PR number to merge.")] int number,
        [Description("Reviewed PR head SHA, obtained from read_pull.head_sha.")] string expectedHeadSha,
        [Description("merge, squash, rebase, rebase-merge, or fast-forward-only. Defaults to merge.")] string mergeStyle = "merge",
        CancellationToken ct = default)
        => ExposeWriteErrorAsync(async () =>
        {
            var pull = await gitea.MergePullRequestAsync(owner, repo, number, expectedHeadSha, mergeStyle, ct);
            return new { number = pull.Number, merged = pull.Merged, merge_commit_sha = pull.MergeCommitSha, html_url = pull.HtmlUrl };
        });

    [McpServerTool]
    [Description(
        "Read a unified diff either for one commit (commitSha) or between two refs (baseBranch and headBranch). " +
        "Pass exactly one mode: commitSha, or both baseBranch and headBranch. Branch comparisons reuse Gitea's compare API.")]
    public async Task<object> get_diff(
        [Description("Repository owner login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Commit SHA for single-commit diff mode. Do not combine with baseBranch/headBranch.")] string? commitSha = null,
        [Description("Base branch/ref for comparison mode.")] string? baseBranch = null,
        [Description("Head branch/ref for comparison mode.")] string? headBranch = null,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        var commitMode = !string.IsNullOrWhiteSpace(commitSha);
        var hasAnyCompareRef = !string.IsNullOrWhiteSpace(baseBranch) || !string.IsNullOrWhiteSpace(headBranch);
        var compareMode = !string.IsNullOrWhiteSpace(baseBranch) && !string.IsNullOrWhiteSpace(headBranch);
        if ((commitMode && hasAnyCompareRef) || (!commitMode && !compareMode))
            throw new ArgumentException("Pass commitSha, or pass both baseBranch and headBranch; do not mix these modes.");

        string diff;
        if (commitMode)
        {
            GiteaWritePolicy.ValidateRefName(commitSha!);
            diff = await gitea.GetCommitDiffAsync(owner, repo, commitSha!, _options.MaxFileBytes, ct)
                ?? throw new KeyNotFoundException($"Commit diff for {commitSha} was not available.");
        }
        else
        {
            GiteaWritePolicy.ValidateRefName(baseBranch!);
            GiteaWritePolicy.ValidateRefName(headBranch!);
            diff = await gitea.GetCompareDiffAsync(owner, repo, baseBranch!, headBranch!, _options.MaxFileBytes, ct);
        }

        return new { diff };
    }

    private static object FormatCommit(GiteaFilesResponse result, IReadOnlyList<string> paths) => new
    {
        commit_sha = result.Commit?.Sha,
        commit_url = result.Commit?.HtmlUrl,
        message = result.Commit?.Message,
        files = paths,
        cleanup_warning = result.CleanupWarning,
    };

    private static async Task<object> ExposeWriteErrorAsync(Func<Task<object>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception error)
        {
            // The MCP SDK intentionally hides ordinary exception messages. McpException marks
            // this as a safe, actionable tool error while keeping protocol errors distinct.
            throw new McpException(error.Message, error);
        }
    }
}
