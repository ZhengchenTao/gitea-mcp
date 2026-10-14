using GiteaMcp.Config;
using GiteaMcp.Services.Models;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GiteaMcp.Services;

/// <summary>
/// Gitea REST API 封装。
/// 使用 HttpClientFactory 注入：读请求走现有 AdminPat 客户端，写请求只能走独立 WritePat 客户端。
/// 所有公开方法在 Gitea 返回错误时抛出结构化异常，不会静默返回空值。
/// </summary>
public class GiteaApiClient
{
    private readonly HttpClient _http;
    private readonly HttpClient _writeHttp;
    private readonly GiteaOptions _opts;
    private readonly ILogger<GiteaApiClient> _logger;
    private readonly GiteaWritePolicy _writePolicy;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public GiteaApiClient(
        IHttpClientFactory factory,
        IOptions<GiteaOptions> opts,
        ILogger<GiteaApiClient> logger,
        GiteaWritePolicy writePolicy)
    {
        _opts = opts.Value;
        _logger = logger;
        _writePolicy = writePolicy;
        _http = factory.CreateClient("gitea");
        _writeHttp = factory.CreateClient("gitea-write");
    }

    // ───────────── Repos ─────────────

    /// <summary>
    /// 搜索所有仓库。owner 为 null 时搜索全部（含 org）。
    /// visibility: "public" | "private" | "all"
    /// </summary>
    public async Task<List<GiteaRepo>> SearchReposAsync(
        string? owner, string visibility, int limit, CancellationToken ct = default)
    {
        // Gitea /repos/search 支持 limit / private / uid / topic 等参数
        var url = $"/api/v1/repos/search?limit={limit}&include_desc=true";

        if (!string.IsNullOrWhiteSpace(owner))
        {
            // /repos/search 没有"按 owner 名过滤"的参数，只能用 uid（owner 的数字 id）。
            // 之前误把 owner 塞进 q——那是按仓库名做文本搜索，所以 owner 过滤永远返回空。
            var uid = await ResolveOwnerIdAsync(owner, ct);
            url += $"&uid={uid}&exclusive=true";
        }

        if (visibility == "private")
            url += "&private=true";
        else if (visibility == "public")
            url += "&private=false";
        // "all" 不加 private 参数，admin PAT 能看到所有

        var result = await GetAsync<GiteaRepoSearchResult>(url, ct);
        return result.Data;
    }

    /// <summary>读取单个仓库元数据</summary>
    public Task<GiteaRepo> GetRepoAsync(string owner, string repo, CancellationToken ct = default)
        => GetAsync<GiteaRepo>($"/api/v1/repos/{owner}/{repo}", ct);

    /// <summary>
    /// 把 owner 登录名解析成数字 id（user 或 org 都支持）。
    /// 先按 user 查，404 再按 org 查——Gitea 里 org 也是一种 user，但端点可能区分。
    /// </summary>
    private async Task<long> ResolveOwnerIdAsync(string owner, CancellationToken ct)
    {
        try
        {
            var user = await GetAsync<GiteaUser>($"/api/v1/users/{Uri.EscapeDataString(owner)}", ct);
            return user.Id;
        }
        catch (KeyNotFoundException)
        {
            var org = await GetAsync<GiteaOrg>($"/api/v1/orgs/{Uri.EscapeDataString(owner)}", ct);
            return org.Id;
        }
    }

    // ───────────── Tree & File ─────────────

    /// <summary>获取仓库文件树</summary>
    public async Task<GiteaTree> GetTreeAsync(
        string owner, string repo, string @ref, bool recursive, CancellationToken ct = default)
    {
        var url = $"/api/v1/repos/{owner}/{repo}/git/trees/{Uri.EscapeDataString(@ref)}";
        if (recursive) url += "?recursive=true";
        return await GetAsync<GiteaTree>(url, ct);
    }

    /// <summary>
    /// 读取文件原始内容（字节），支持 maxBytes 截断。
    /// 返回 (content, truncated)。
    /// </summary>
    public async Task<(string Content, bool Truncated)> GetRawFileAsync(
        string owner, string repo, string @ref, string path, int maxBytes, CancellationToken ct = default)
    {
        var url = $"/api/v1/repos/{owner}/{repo}/raw/{Uri.EscapeDataString(@ref)}/{path.TrimStart('/')}";
        var response = await SendAsync(url, ct);

        // 读最多 maxBytes + 1 字节，用来判断是否截断
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes + 1];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(), ct);

        bool truncated = bytesRead > maxBytes;
        var actual = truncated ? buffer[..maxBytes] : buffer[..bytesRead];
        return (System.Text.Encoding.UTF8.GetString(actual), truncated);
    }

    // ───────────── Branches & Commits ─────────────

    public Task<List<GiteaBranch>> GetBranchesAsync(
        string owner, string repo, CancellationToken ct = default)
        => GetAsync<List<GiteaBranch>>($"/api/v1/repos/{owner}/{repo}/branches", ct);

    /// <summary>Create a new branch from a branch, tag, or commit SHA.</summary>
    public async Task<GiteaBranch> CreateBranchAsync(
        string owner, string repo, string newBranch, string? fromRef, CancellationToken ct = default)
    {
        _writePolicy.RequireWriteAccess(owner, repo);
        _writePolicy.ValidateNewBranchName(newBranch);

        var repository = await GetRepoAsync(owner, repo, ct);
        if (string.Equals(repository.DefaultBranch, newBranch, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Creating the repository default branch '{newBranch}' through this tool is disabled.");

        var existingBranches = await GetBranchesAsync(owner, repo, ct);
        if (existingBranches.Any(b => string.Equals(b.Name, newBranch, StringComparison.OrdinalIgnoreCase)))
            throw new GiteaWriteConflictException($"Branch '{newBranch}' already exists in {owner}/{repo}.");

        var source = string.IsNullOrWhiteSpace(fromRef) ? repository.DefaultBranch : fromRef.Trim();
        GiteaWritePolicy.ValidateRefName(source);

        try
        {
            return await PostWriteAsync<GiteaBranch>(
                $"{RepoApiPath(owner, repo)}/branches",
                new { new_branch_name = newBranch, old_ref_name = source },
                "create branch", ct);
        }
        catch (Exception ex) when (IsTransientWriteFailure(ex, ct))
        {
            throw new GiteaWriteOutcomeUnknownException(
                $"Branch creation outcome is unknown for '{newBranch}'. List repository branches before retrying.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new GiteaWriteOutcomeUnknownException(
                $"Branch creation was cancelled after the request started. Check whether '{newBranch}' exists before retrying.");
        }
    }

    /// <summary>
    /// Create one Git commit containing all requested file operations, then CAS the work branch HEAD.
    /// The batch is staged on a unique temporary ref; only the conditional ref update changes the work branch.
    /// </summary>
    public async Task<GiteaFilesResponse> CommitFilesAsync(
        string owner,
        string repo,
        string branch,
        string expectedHeadSha,
        string? message,
        IReadOnlyList<GiteaWriteFileChange> changes,
        CancellationToken ct = default)
    {
        _writePolicy.RequireWriteAccess(owner, repo);
        if (!IsBlobSha(expectedHeadSha))
            throw new ArgumentException("expectedHeadSha must be a 40- or 64-character commit SHA.", nameof(expectedHeadSha));
        var currentBranch = await EnsureWritableBranchAsync(owner, repo, branch, ct);
        var currentHeadSha = currentBranch.Commit?.Id;
        if (!string.Equals(currentHeadSha, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
            throw new GiteaWriteConflictException(
                $"Branch HEAD conflict for {branch}: expectedHeadSha no longer matches. Read the working branch again before retrying.");

        if (changes is null || changes.Count == 0)
            throw new ArgumentException("Provide at least one file operation.", nameof(changes));
        if (changes.Count > _opts.MaxWriteFiles)
            throw new ArgumentException($"A commit may contain at most {_opts.MaxWriteFiles} file operations.", nameof(changes));
        if (string.IsNullOrWhiteSpace(message)) message = "Update files via Gitea MCP";
        message = message.Trim();
        if (_writePolicy.GetUtf8ByteCount(message, nameof(message)) > 2_000)
            throw new ArgumentException("Commit message must be at most 2,000 UTF-8 bytes.", nameof(message));

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalContentBytes = 0;
        var apiFiles = new List<object>(changes.Count);

        foreach (var change in changes)
        {
            if (change is null) throw new ArgumentException("File operation entries cannot be null.", nameof(changes));
            GiteaWritePolicy.ValidateFilePath(change.Path);
            if (!seenPaths.Add(change.Path))
                throw new ArgumentException($"Duplicate file path in one commit: {change.Path}.", nameof(changes));

            var operation = change.Operation.Trim().ToLowerInvariant();
            if (operation is not ("create" or "update" or "delete"))
                throw new ArgumentException($"Unsupported operation for {change.Path}; use create, update, or delete.", nameof(changes));

            var hasExpectedSha = !string.IsNullOrWhiteSpace(change.ExpectedSha);
            if (operation is "update" or "delete")
            {
                if (!hasExpectedSha || !IsBlobSha(change.ExpectedSha!))
                    throw new ArgumentException($"Operation '{operation}' requires a 40- or 64-character expectedSha for {change.Path}.", nameof(changes));
            }
            else if (hasExpectedSha)
            {
                throw new ArgumentException($"Operation 'create' must omit expectedSha for {change.Path}.", nameof(changes));
            }

            if (operation == "delete")
            {
                if (change.Content is not null)
                    throw new ArgumentException($"Delete operation must omit content for {change.Path}.", nameof(changes));
            }
            else if (change.Content is null)
            {
                throw new ArgumentException($"Operation '{operation}' requires text content for {change.Path}.", nameof(changes));
            }

            var current = await GetFileMetadataAsync(owner, repo, branch, change.Path, ct);
            if (operation == "create")
            {
                if (current is not null)
                    throw new GiteaWriteConflictException($"File {change.Path} already exists on branch {branch}; use update with its current SHA.");
                apiFiles.Add(new { operation, path = change.Path, content = EncodeText(change.Content!) });
                var bytes = _writePolicy.GetUtf8ByteCount(change.Content!, nameof(change.Content));
                CheckContentSize(change.Path, bytes);
                totalContentBytes += bytes;
            }
            else
            {
                if (current is null)
                    throw new KeyNotFoundException($"File {change.Path} does not exist on branch {branch}.");
                if (!string.Equals(current.Sha, change.ExpectedSha, StringComparison.OrdinalIgnoreCase))
                    throw new GiteaWriteConflictException(
                        $"SHA conflict for {change.Path}: expectedSha does not match the current file version. Read the file again before retrying.");
                if (!string.Equals(current.Type, "file", StringComparison.OrdinalIgnoreCase))
                    throw new GiteaWriteConflictException($"Path {change.Path} is not a regular file and cannot be changed by this tool.");

                if (operation == "delete")
                {
                    apiFiles.Add(new { operation, path = change.Path, sha = change.ExpectedSha });
                }
                else
                {
                    apiFiles.Add(new { operation, path = change.Path, sha = change.ExpectedSha, content = EncodeText(change.Content!) });
                    var bytes = _writePolicy.GetUtf8ByteCount(change.Content!, nameof(change.Content));
                    CheckContentSize(change.Path, bytes);
                    totalContentBytes += bytes;
                }
            }

            if (totalContentBytes > _opts.MaxWriteRequestBytes)
                throw new ArgumentException($"Combined file content exceeds Gitea:MaxWriteRequestBytes ({_opts.MaxWriteRequestBytes} bytes).", nameof(changes));
        }

        // Gitea 1.27's multi-file contents API has per-file SHA checks, but no expected old branch HEAD.
        // Stage the one-commit batch on a unique temporary ref, then CAS-update the requested branch
        // with PUT branches/{branch} + old_commit_id. This closes the preflight/write race.
        var stagingBranch = $"mcp-tmp-{Guid.NewGuid():N}";
        var payload = new { branch, new_branch = stagingBranch, message, files = apiFiles };
        GiteaFilesResponse result;
        try
        {
            result = await CreateStagedCommitAsync(
                owner, repo, branch, stagingBranch, payload, expectedHeadSha, message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new GiteaWriteOutcomeUnknownException(
                $"The request was cancelled while staging. The target branch was not advanced by this call, but temporary ref '{stagingBranch}' may exist; inspect refs before retrying.");
        }
        var commitSha = result.Commit?.Sha;
        if (string.IsNullOrWhiteSpace(commitSha) || !IsBlobSha(commitSha))
        {
            var cleanupWarning = await TryDeleteStagingBranchAsync(owner, repo, stagingBranch, ct);
            throw new GiteaWriteOutcomeUnknownException(
                AddCleanupWarning("Gitea created the staging commit but returned no usable commit SHA. Check the branch and diff before retrying.", cleanupWarning));
        }

        try
        {
            await EnsureStagedCommitParentAsync(owner, repo, result.Commit!, expectedHeadSha, ct);
        }
        catch (GiteaWriteConflictException ex)
        {
            var cleanupWarning = await TryDeleteStagingBranchAsync(owner, repo, stagingBranch, ct);
            throw new GiteaWriteConflictException(AddCleanupWarning(ex.Message, cleanupWarning));
        }
        catch (Exception)
        {
            var cleanupWarning = await TryDeleteStagingBranchAsync(owner, repo, stagingBranch, ct);
            throw new GiteaWriteOutcomeUnknownException(AddCleanupWarning(
                $"The staged commit parent could not be verified. The target branch has not been updated; inspect temporary branch '{stagingBranch}' before retrying.", cleanupWarning));
        }

        try
        {
            await UpdateBranchRefCasAsync(owner, repo, branch, expectedHeadSha, commitSha, ct);
        }
        catch (GiteaWriteOutcomeUnknownException ex)
        {
            // Keep the staging ref reachable until the caller verifies whether the CAS landed.
            throw new GiteaWriteOutcomeUnknownException(
                $"{ex.Message} Temporary branch '{stagingBranch}' was retained for recovery.");
        }
        catch (GiteaWriteConflictException ex)
        {
            var cleanupWarning = await TryDeleteStagingBranchAsync(owner, repo, stagingBranch, ct);
            throw new GiteaWriteConflictException(AddCleanupWarning(ex.Message, cleanupWarning));
        }
        catch (UnauthorizedAccessException ex)
        {
            var cleanupWarning = await TryDeleteStagingBranchAsync(owner, repo, stagingBranch, ct);
            throw new UnauthorizedAccessException(AddCleanupWarning(ex.Message, cleanupWarning), ex);
        }
        catch (KeyNotFoundException ex)
        {
            var cleanupWarning = await TryDeleteStagingBranchAsync(owner, repo, stagingBranch, ct);
            throw new KeyNotFoundException(AddCleanupWarning(ex.Message, cleanupWarning), ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new GiteaWriteOutcomeUnknownException(
                $"The write was cancelled during branch compare-and-swap. Verify branch '{branch}' and temporary ref '{stagingBranch}' before retrying.");
        }

        result.CleanupWarning = await TryDeleteStagingBranchAsync(owner, repo, stagingBranch, ct);
        return result;
    }

    /// <summary>Create a new file or update an existing one using an expected blob SHA.</summary>
    public async Task<GiteaFilesResponse> UpsertFileAsync(
        string owner,
        string repo,
        string path,
        string content,
        string branch,
        string expectedHeadSha,
        string? expectedSha,
        string? message,
        CancellationToken ct = default)
    {
        _writePolicy.RequireWriteAccess(owner, repo);
        _writePolicy.ValidateWritableBranchName(branch);
        GiteaWritePolicy.ValidateFilePath(path);
        var current = await GetFileMetadataAsync(owner, repo, branch, path, ct);
        GiteaWriteFileChange change;

        if (current is null)
        {
            if (!string.IsNullOrWhiteSpace(expectedSha))
                throw new GiteaWriteConflictException($"File {path} does not exist on branch {branch}; omit expectedSha to create it.");
            change = new GiteaWriteFileChange { Operation = "create", Path = path, Content = content };
        }
        else
        {
            if (string.IsNullOrWhiteSpace(expectedSha))
                throw new ArgumentException($"Updating {path} requires expectedSha. Read the file first and pass its current SHA.", nameof(expectedSha));
            if (!string.Equals(current.Sha, expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new GiteaWriteConflictException($"SHA conflict for {path}: expectedSha does not match the current file version.");
            change = new GiteaWriteFileChange { Operation = "update", Path = path, Content = content, ExpectedSha = expectedSha };
        }

        return await CommitFilesAsync(owner, repo, branch, expectedHeadSha, message,
            new[] { change }, ct);
    }

    public Task<GiteaFilesResponse> DeleteFileAsync(
        string owner, string repo, string path, string expectedSha, string branch, string expectedHeadSha,
        string? message, CancellationToken ct = default)
        => CommitFilesAsync(owner, repo, branch, expectedHeadSha, message,
            new[] { new GiteaWriteFileChange { Operation = "delete", Path = path, ExpectedSha = expectedSha } }, ct);

    public async Task<GiteaPullRequest> CreatePullRequestAsync(
        string owner, string repo, string head, string baseBranch, string title, string? body,
        CancellationToken ct = default)
    {
        _writePolicy.RequireWriteAccess(owner, repo);
        GiteaWritePolicy.ValidateBranchName(head);
        GiteaWritePolicy.ValidateBranchName(baseBranch);
        if (string.Equals(head, baseBranch, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pull request head and base must be different branches.");
        if (string.IsNullOrWhiteSpace(title) || _writePolicy.GetUtf8ByteCount(title, nameof(title)) > 255)
            throw new ArgumentException("Pull request title is required and must be at most 255 UTF-8 bytes.", nameof(title));
        if (_writePolicy.GetUtf8ByteCount(body ?? string.Empty, nameof(body)) > _opts.MaxWriteRequestBytes)
            throw new ArgumentException("Pull request description exceeds Gitea:MaxWriteRequestBytes.", nameof(body));

        try
        {
            return await PostWriteAsync<GiteaPullRequest>(
                $"{RepoApiPath(owner, repo)}/pulls",
                new { head, @base = baseBranch, title, body = body ?? string.Empty },
                "create pull request", ct);
        }
        catch (Exception ex) when (IsTransientWriteFailure(ex, ct))
        {
            throw new GiteaWriteOutcomeUnknownException(
                $"Pull request creation outcome is unknown for {head} → {baseBranch}. Check open PRs for this head/base pair before retrying.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new GiteaWriteOutcomeUnknownException(
                $"Pull request creation was cancelled after the request started. Check open PRs for {head} → {baseBranch} before retrying.");
        }
    }

    public async Task<GiteaPullRequest> MergePullRequestAsync(
        string owner, string repo, int number, string expectedHeadSha, string mergeStyle,
        CancellationToken ct = default)
    {
        _writePolicy.RequireWriteAccess(owner, repo);
        if (number <= 0) throw new ArgumentException("PR number must be positive.", nameof(number));
        if (!IsBlobSha(expectedHeadSha))
            throw new ArgumentException("expectedHeadSha must be a 40- or 64-character PR head SHA.", nameof(expectedHeadSha));
        if (mergeStyle is not ("merge" or "squash" or "rebase" or "rebase-merge" or "fast-forward-only"))
            throw new ArgumentException("mergeStyle must be merge, squash, rebase, rebase-merge, or fast-forward-only.", nameof(mergeStyle));

        var pull = await GetPullAsync(owner, repo, number, ct);
        if (!string.Equals(pull.Head?.Sha, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
            throw new GiteaWriteConflictException("PR head changed. Read the PR and review its new diff before merging.");
        if (pull.Merged) return pull;
        if (pull.State != "open") throw new GiteaWriteConflictException("The PR is closed and has not been merged.");

        var status = await GetAsync<JsonElement>(
            $"{RepoApiPath(owner, repo)}/commits/{expectedHeadSha}/status", ct);
        if (status.TryGetProperty("total_count", out var count) && count.GetInt32() > 0 &&
            status.TryGetProperty("state", out var state) && state.GetString() != "success")
            throw new GiteaWriteConflictException($"PR CI checks are {state.GetString()}; wait for successful checks before merging.");

        var url = $"{RepoApiPath(owner, repo)}/pulls/{number}/merge";
        try
        {
            using var response = await _writeHttp.PostAsJsonAsync(url, new
            {
                @do = mergeStyle,
                head_commit_id = expectedHeadSha,
                force_merge = false,
                delete_branch_after_merge = false,
                merge_when_checks_succeed = false,
            }, JsonOpts, ct);
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
                throw new GiteaWriteConflictException("Gitea rejected the merge: check conflicts, required reviews, CI and repository merge settings.");
            EnsureWriteSuccess(response, url, "merge pull request");
        }
        catch (Exception ex) when (IsTransientWriteFailure(ex, ct))
        {
            try
            {
                var observed = await GetPullAsync(owner, repo, number, ct);
                if (observed.Merged) return observed;
            }
            catch (Exception) { /* Preserve the unknown outcome; never replay a merge. */ }
            throw new GiteaWriteOutcomeUnknownException("PR merge outcome is unknown. Read the PR before retrying; the merge request was not replayed.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new GiteaWriteOutcomeUnknownException("PR merge was cancelled after the request started. Read the PR before retrying.");
        }

        try
        {
            pull = await GetPullAsync(owner, repo, number, ct);
            if (pull.Merged) return pull;
        }
        catch (Exception) { /* A successful POST must not be replayed if verification fails. */ }
        throw new GiteaWriteOutcomeUnknownException("Gitea accepted the merge but its final state could not be verified. Read the PR before retrying.");
    }

    /// <summary>Get a branch diff as plain unified diff text.</summary>
    public async Task<string> GetCompareDiffAsync(
        string owner, string repo, string baseBranch, string headBranch,
        int maxBytes = 1024 * 1024, CancellationToken ct = default)
    {
        var comparison = Uri.EscapeDataString($"{baseBranch}...{headBranch}");
        var url = $"{RepoApiPath(owner, repo)}/compare/{comparison}?output=diff";
        var response = await SendAsync(url, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var (text, truncated) = await ReadUpToAsync(stream, maxBytes, ct);
        return truncated ? text + "\n[...diff truncated...]" : text;
    }

    public async Task<List<GiteaCommitSummary>> GetCommitsAsync(
        string owner, string repo, string? @ref, string? since, int limit, CancellationToken ct = default)
    {
        var url = $"/api/v1/repos/{owner}/{repo}/commits?limit={limit}&stat=false&files=false&verification=false";
        if (!string.IsNullOrWhiteSpace(@ref)) url += $"&sha={Uri.EscapeDataString(@ref)}";
        if (!string.IsNullOrWhiteSpace(since)) url += $"&since={Uri.EscapeDataString(since)}";
        return await GetAsync<List<GiteaCommitSummary>>(url, ct);
    }

    public Task<GiteaCommitFull> GetCommitAsync(
        string owner, string repo, string sha, CancellationToken ct = default)
        => GetAsync<GiteaCommitFull>($"/api/v1/repos/{owner}/{repo}/git/commits/{sha}", ct);

    /// <summary>
    /// 获取单个 commit 的统一 diff 文本。
    /// Gitea 的 commit 元数据端点的 files 只含 filename+status，没有行数/patch，
    /// 真正的 diff 要走 .diff 端点：GET /repos/{owner}/{repo}/git/commits/{sha}.diff。
    /// 取不到（端点缺失/空）时返回 null，调用方据此降级，不抛异常。
    /// 最多读 maxBytes（默认 1MB）防爆内存。
    /// </summary>
    public async Task<string?> GetCommitDiffAsync(
        string owner, string repo, string sha, int maxBytes = 1024 * 1024,
        CancellationToken ct = default)
    {
        var url = $"/api/v1/repos/{owner}/{repo}/git/commits/{Uri.EscapeDataString(sha)}.diff";
        try
        {
            var response = await SendAsync(url, ct);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var (text, truncated) = await ReadUpToAsync(stream, maxBytes, ct);
            return truncated ? text + "\n[...diff truncated at 1MB...]" : text;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    // ───────────── Issues ─────────────

    public Task<List<GiteaIssue>> GetIssuesAsync(
        string owner, string repo, string state, int limit, CancellationToken ct = default)
        => GetAsync<List<GiteaIssue>>(
            $"/api/v1/repos/{owner}/{repo}/issues?type=issues&state={state}&limit={limit}", ct);

    public Task<GiteaIssue> GetIssueAsync(
        string owner, string repo, int number, CancellationToken ct = default)
        => GetAsync<GiteaIssue>($"/api/v1/repos/{owner}/{repo}/issues/{number}", ct);

    public Task<List<GiteaComment>> GetIssueCommentsAsync(
        string owner, string repo, int number, CancellationToken ct = default)
        => GetAsync<List<GiteaComment>>($"/api/v1/repos/{owner}/{repo}/issues/{number}/comments", ct);

    // ───────────── Pull Requests ─────────────

    public Task<List<GiteaPullRequest>> GetPullsAsync(
        string owner, string repo, string state, int limit, CancellationToken ct = default)
        => GetAsync<List<GiteaPullRequest>>(
            $"/api/v1/repos/{owner}/{repo}/pulls?state={state}&limit={limit}", ct);

    public Task<GiteaPullRequest> GetPullAsync(
        string owner, string repo, int number, CancellationToken ct = default)
        => GetAsync<GiteaPullRequest>($"/api/v1/repos/{owner}/{repo}/pulls/{number}", ct);

    public Task<List<GiteaComment>> GetPullCommentsAsync(
        string owner, string repo, int number, CancellationToken ct = default)
        => GetAsync<List<GiteaComment>>($"/api/v1/repos/{owner}/{repo}/issues/{number}/comments", ct);

    public Task<List<GiteaPrFile>> GetPullFilesAsync(
        string owner, string repo, int number, CancellationToken ct = default)
        => GetAsync<List<GiteaPrFile>>($"/api/v1/repos/{owner}/{repo}/pulls/{number}/files", ct);

    // ───────────── Orgs ─────────────

    /// <summary>
    /// 列出所有组织。用 /api/v1/orgs（任意 read PAT 即可，返回当前用户可见的 org）；
    /// 仅当带 admin scope 的 PAT 才能访问 /admin/orgs。本服务的 PAT 设计为只读，
    /// 因此不走 /admin/* 端点。
    /// 注意：Gitea 没有 /orgs/search 端点（org 不像 repo/user 那样支持 search），
    /// /orgs/search 会被路由成 /orgs/{org}（org="search"）→ 404。
    /// </summary>
    public Task<List<GiteaOrg>> GetOrgsAsync(int limit, CancellationToken ct = default)
        // /api/v1/orgs 直接返回裸数组 [...]，不是 { ok, data } 包装
        => GetAsync<List<GiteaOrg>>($"/api/v1/orgs?limit={limit}", ct);

    public Task<GiteaOrg> GetOrgAsync(string name, CancellationToken ct = default)
        => GetAsync<GiteaOrg>($"/api/v1/orgs/{name}", ct);

    // ───────────── Packages ─────────────

    public Task<List<GiteaPackage>> GetPackagesAsync(
        string owner, string? type, int limit, CancellationToken ct = default)
    {
        var url = $"/api/v1/packages/{owner}?limit={limit}";
        if (!string.IsNullOrWhiteSpace(type)) url += $"&type={type}";
        return GetAsync<List<GiteaPackage>>(url, ct);
    }

    public Task<GiteaPackage> GetPackageAsync(
        string owner, string type, string name, string version, CancellationToken ct = default)
        => GetAsync<GiteaPackage>($"/api/v1/packages/{owner}/{type}/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}", ct);

    // ───────────── Actions ─────────────

    public Task<GiteaWorkflowList> GetWorkflowsAsync(string owner, string repo, CancellationToken ct = default)
        => GetAsync<GiteaWorkflowList>($"{RepoApiPath(owner, repo)}/actions/workflows", ct);

    public async Task<GiteaWorkflowDispatchResult> DispatchWorkflowAsync(
        string owner, string repo, string workflowId, string reference,
        IReadOnlyDictionary<string, string>? inputs, CancellationToken ct = default)
    {
        _writePolicy.RequireWriteAccess(owner, repo);
        GiteaWritePolicy.ValidateFilePath(workflowId);
        if (workflowId.Length > 200 || workflowId.Contains('/'))
            throw new ArgumentException("workflowId must be a filename/ID returned by list_workflows, without directories.", nameof(workflowId));
        GiteaWritePolicy.ValidateRefName(reference);
        if (inputs is not null)
        {
            if (inputs.Count > 50) throw new ArgumentException("At most 50 workflow inputs are allowed.", nameof(inputs));
            var bytes = 0;
            foreach (var (key, value) in inputs)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > 100 || value is null)
                    throw new ArgumentException("Workflow inputs require nonempty keys (at most 100 characters) and string values.", nameof(inputs));
                bytes += _writePolicy.GetUtf8ByteCount(key, nameof(inputs)) + _writePolicy.GetUtf8ByteCount(value, nameof(inputs));
                if (bytes > _opts.MaxWriteRequestBytes)
                    throw new ArgumentException("Workflow input content exceeds Gitea:MaxWriteRequestBytes.", nameof(inputs));
            }
        }
        var url = $"{RepoApiPath(owner, repo)}/actions/workflows/{Uri.EscapeDataString(workflowId)}/dispatches?return_run_details=true";
        try
        {
            using var response = await _writeHttp.PostAsJsonAsync(url,
                new { @ref = reference, inputs = inputs ?? new Dictionary<string, string>() }, JsonOpts, ct);
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new ArgumentException("Gitea rejected the workflow dispatch. Check workflow_dispatch support, the ref and required inputs.");
            EnsureWriteSuccess(response, url, "dispatch workflow");
            if (response.StatusCode == HttpStatusCode.NoContent) return new GiteaWorkflowDispatchResult();
            return await response.Content.ReadFromJsonAsync<GiteaWorkflowDispatchResult>(JsonOpts, ct)
                ?? new GiteaWorkflowDispatchResult();
        }
        catch (Exception ex) when (IsTransientWriteFailure(ex, ct) || ex is JsonException ||
                                   ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            throw new GiteaWriteOutcomeUnknownException("Workflow dispatch outcome is unknown. Check list_workflow_runs before retrying; the dispatch request was not replayed.");
        }
    }

    public async Task<GiteaWorkflowRun> RerunWorkflowAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
    {
        _writePolicy.RequireWriteAccess(owner, repo);
        if (runId <= 0) throw new ArgumentException("runId must be positive.", nameof(runId));
        try
        {
            return await PostWriteAsync<GiteaWorkflowRun>(
                $"{RepoApiPath(owner, repo)}/actions/runs/{runId}/rerun", new { }, "rerun workflow", ct);
        }
        catch (Exception ex) when (IsTransientWriteFailure(ex, ct) || ex is JsonException ||
                                   ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            throw new GiteaWriteOutcomeUnknownException("Workflow rerun outcome is unknown. Check list_workflow_runs/read_run_log before retrying; the rerun request was not replayed.");
        }
    }

    public async Task<GiteaWorkflowRunList> GetWorkflowRunsAsync(
        string owner, string repo, string? branch, string? status, int limit, CancellationToken ct = default)
    {
        var url = $"/api/v1/repos/{owner}/{repo}/actions/runs?limit={limit}";
        if (!string.IsNullOrWhiteSpace(branch)) url += $"&branch={Uri.EscapeDataString(branch)}";
        if (!string.IsNullOrWhiteSpace(status)) url += $"&status={status}";
        return await GetAsync<GiteaWorkflowRunList>(url, ct);
    }

    public Task<GiteaWorkflowRun> GetWorkflowRunAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
        => GetAsync<GiteaWorkflowRun>($"/api/v1/repos/{owner}/{repo}/actions/runs/{runId}", ct);

    public Task<GiteaWorkflowJobList> GetRunJobsAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
        => GetAsync<GiteaWorkflowJobList>($"/api/v1/repos/{owner}/{repo}/actions/runs/{runId}/jobs", ct);

    /// <summary>
    /// 获取单个 job 的日志（纯文本）。Gitea 1.24+ 端点：
    ///   GET /api/v1/repos/{owner}/{repo}/actions/jobs/{jobId}/logs
    /// 注意：
    ///   - Gitea 没有 run 级日志端点，run 日志 = 各 job 日志拼接（见 ActionsTools）。
    ///   - job 是 repo 级资源，不是 /runs/{run}/jobs/{job}/logs 这种嵌套路径。
    /// 最多读 maxBytes（默认 1MB）防爆内存。
    /// </summary>
    public async Task<string> GetJobLogAsync(
        string owner, string repo, long jobId, int maxBytes = 128 * 1024, bool tail = true,
        CancellationToken ct = default)
    {
        var url = $"/api/v1/repos/{owner}/{repo}/actions/jobs/{jobId}/logs";

        try
        {
            var response = await SendAsync(url, ct);
            using var stream = await response.Content.ReadAsStreamAsync(ct);

            if (tail)
            {
                // CI 日志失败信息都在末尾，取尾部更有用，也避免整段塞爆 MCP 返回上限
                var (text, cut) = await ReadTailAsync(stream, maxBytes, ct);
                return cut ? "[...earlier log truncated...]\n" + text : text;
            }

            var (head, truncated) = await ReadUpToAsync(stream, maxBytes, ct);
            return truncated ? head + "\n[...log truncated...]" : head;
        }
        catch (KeyNotFoundException)
        {
            // job 日志可能还未生成（job 排队中/刚开始）
            return "[Log not yet available]";
        }
    }

    /// <summary>
    /// 读取 stream 的尾部最多 maxBytes 字节（失败日志重点在末尾）。
    /// 全量读入有硬上限防爆内存；为避免从多字节字符中间切开导致首行乱码，
    /// 截断时从尾窗口内第一个换行之后开始。返回 (text, 是否截掉了前面)。
    /// </summary>
    private static async Task<(string Text, bool Cut)> ReadTailAsync(
        Stream stream, int maxBytes, CancellationToken ct)
    {
        const int HardCeiling = 16 * 1024 * 1024; // 16MB，超大日志的兜底
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buf.AsMemory(), ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > HardCeiling) break;
        }

        var all = ms.GetBuffer();
        int len = (int)ms.Length;
        if (len <= maxBytes)
            return (System.Text.Encoding.UTF8.GetString(all, 0, len), false);

        int start = len - maxBytes;
        // 推进到下一个换行，避免半个字符/半行
        while (start < len && all[start] != (byte)'\n') start++;
        if (start < len) start++; // 跳过换行本身
        return (System.Text.Encoding.UTF8.GetString(all, start, len - start), true);
    }

    /// <summary>
    /// 从 stream 读最多 maxBytes 字节，循环读满——单次 ReadAsync 可能返回不足量
    /// （未到 EOF 也可能只给一部分），不循环会把日志提前截断。
    /// 返回 (text, truncated)，truncated 表示还有更多内容被丢弃。
    /// </summary>
    private static async Task<(string Text, bool Truncated)> ReadUpToAsync(
        Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[maxBytes];
        int total = 0;
        while (total < maxBytes)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), ct);
            if (n == 0) break;
            total += n;
        }

        // 读满了 maxBytes 还能再读到字节，说明被截断
        bool truncated = total == maxBytes
            && await stream.ReadAsync(new byte[1].AsMemory(), ct) > 0;

        return (System.Text.Encoding.UTF8.GetString(buffer, 0, total), truncated);
    }

    // ───────────── Code search ─────────────

    /// <summary>
    /// 代码搜索。需要 Gitea 启用 code indexer（app.ini [indexer] REPO_INDEXER_ENABLED = true）。
    /// indexer 未启用时 Gitea 返回 404，调用方应降级并告知 Claude（不要 swallow 到空数组）。
    /// </summary>
    /// <remarks>
    /// Gitea API：
    ///   GET /api/v1/repos/search-code?q=&owner=&repo=&limit=
    /// 未启用 indexer 时端点返回 404，由 EnsureSuccessAsync 抛 KeyNotFoundException，
    /// 上游 SearchTools 捕获此异常返回结构化 indexer-disabled 提示。
    /// </remarks>
    public async Task<GiteaCodeSearchResponse> SearchCodeAsync(
        string query, string? owner, string? repo, int limit, CancellationToken ct = default)
    {
        var url = $"/api/v1/repos/search-code?q={Uri.EscapeDataString(query)}&limit={limit}";
        if (!string.IsNullOrWhiteSpace(owner)) url += $"&owner={Uri.EscapeDataString(owner)}";
        if (!string.IsNullOrWhiteSpace(repo)) url += $"&repo={Uri.EscapeDataString(repo)}";

        return await GetAsync<GiteaCodeSearchResponse>(url, ct);
    }

    // ───────────── Private helpers ─────────────

    private async Task<GiteaBranch> EnsureWritableBranchAsync(
        string owner, string repo, string branchName, CancellationToken ct)
    {
        _writePolicy.ValidateWritableBranchName(branchName);
        GiteaBranch branch;
        try
        {
            branch = await GetAsync<GiteaBranch>(
                $"{RepoApiPath(owner, repo)}/branches/{Uri.EscapeDataString(branchName)}", ct);
        }
        catch (KeyNotFoundException)
        {
            throw new KeyNotFoundException($"Branch '{branchName}' does not exist in {owner}/{repo}.");
        }

        return branch;
    }

    private async Task<GiteaFilesResponse> CreateStagedCommitAsync(
        string owner, string repo, string targetBranch, string stagingBranch, object payload,
        string expectedHeadSha, string message, CancellationToken ct)
    {
        try
        {
            return await PostWriteAsync<GiteaFilesResponse>(
                $"{RepoApiPath(owner, repo)}/contents", payload, "stage commit files", ct);
        }
        catch (Exception ex) when (IsTransientWriteFailure(ex, ct))
        {
            // The POST may have succeeded even though its response was lost. Do not replay it;
            // recover only if the unique staging ref points to our expected parent and message.
            try
            {
                var stagedBranch = await GetAsync<GiteaBranch>(
                    $"{RepoApiPath(owner, repo)}/branches/{Uri.EscapeDataString(stagingBranch)}", ct);
                var stagedSha = stagedBranch.Commit?.Id;
                if (string.IsNullOrWhiteSpace(stagedSha) || !IsBlobSha(stagedSha))
                    throw new GiteaWriteOutcomeUnknownException(
                        $"Batch outcome is unknown. Inspect temporary branch '{stagingBranch}' and do not retry blindly.");

                var stagedCommit = await GetCommitAsync(owner, repo, stagedSha, ct);
                var parents = stagedCommit.Parents ?? [];
                if (!string.Equals(stagedCommit.Commit?.Message.Trim(), message, StringComparison.Ordinal) ||
                    parents.Count != 1 ||
                    !string.Equals(parents[0].Sha, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new GiteaWriteOutcomeUnknownException(
                        $"Batch outcome is unknown. Temporary branch '{stagingBranch}' does not match this request; inspect it before retrying.");
                }

                return new GiteaFilesResponse
                {
                    Commit = new GiteaWriteCommit
                    {
                        Sha = stagedCommit.Sha,
                        Message = stagedCommit.Commit?.Message ?? string.Empty,
                        Parents = parents.Select(p => new GiteaWriteCommitParent { Sha = p.Sha }).ToList(),
                    },
                };
            }
            catch (KeyNotFoundException)
            {
                throw new GiteaWriteOutcomeUnknownException(
                    $"Gitea did not confirm the batch response. The target branch '{targetBranch}' has not been advanced by the compare-and-swap step, but temporary branch '{stagingBranch}' could appear later. Inspect refs before retrying.");
            }
            catch (GiteaWriteOutcomeUnknownException)
            {
                throw;
            }
            catch (Exception recoveryError) when (IsTransientWriteFailure(recoveryError, ct))
            {
                throw new GiteaWriteOutcomeUnknownException(
                    $"Batch outcome is unknown and could not be verified. Inspect temporary branch '{stagingBranch}' and target branch '{targetBranch}' before retrying.");
            }
        }
    }

    private async Task EnsureStagedCommitParentAsync(
        string owner, string repo, GiteaWriteCommit stagedCommit, string expectedHeadSha, CancellationToken ct)
    {
        var parents = stagedCommit.Parents;
        if (parents.Count == 1 && string.Equals(parents[0].Sha, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
            return;

        // The FilesResponse normally includes parent metadata. Resolve it from the commit endpoint if
        // this Gitea-compatible server omits it; never update the target ref without checking the parent.
        var fullCommit = await GetCommitAsync(owner, repo, stagedCommit.Sha, ct);
        var fullParents = fullCommit.Parents ?? [];
        if (fullParents.Count != 1 || !string.Equals(fullParents[0].Sha, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
            throw new GiteaWriteConflictException(
                $"Branch HEAD changed while staging the batch. The staged commit parent does not match expectedHeadSha for {repo}; re-read the branch and rebuild the changes.");
    }

    private async Task UpdateBranchRefCasAsync(
        string owner, string repo, string branch, string expectedHeadSha, string newCommitSha,
        CancellationToken ct)
    {
        try
        {
            await PutBranchRefAsync(owner, repo, branch, expectedHeadSha, newCommitSha, ct);
        }
        catch (GiteaWriteConflictException)
        {
            throw new GiteaWriteConflictException(
                $"Branch HEAD conflict for {branch}: expectedHeadSha no longer matches the server HEAD. Re-read the working branch and rebuild the changes.");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Gitea reports some pre-receive/non-fast-forward rejections as 403. Re-read HEAD
            // to distinguish a lost race from a stable permission/protection denial.
            var observed = await GetBranchHeadForRecoveryAsync(owner, repo, branch, newCommitSha, ct);
            if (string.Equals(observed, newCommitSha, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(observed, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
                throw new GiteaWriteConflictException(
                    $"Branch HEAD conflict for {branch}: it moved to {observed} while applying the compare-and-swap update.");
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
        catch (Exception ex) when (IsTransientWriteFailure(ex, ct))
        {
            var observed = await GetBranchHeadForRecoveryAsync(owner, repo, branch, newCommitSha, ct);
            if (string.Equals(observed, newCommitSha, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(observed, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
                throw new GiteaWriteConflictException(
                    $"Branch HEAD conflict for {branch}: the server now points to {observed}. Re-read before retrying.");

            // The update API's old_commit_id is a server-side compare-and-swap. One retry is safe:
            // if the first request later completes, the old SHA no longer matches and this call cannot overwrite it.
            try
            {
                await PutBranchRefAsync(owner, repo, branch, expectedHeadSha, newCommitSha, ct);
            }
            catch (GiteaWriteConflictException)
            {
                observed = await GetBranchHeadForRecoveryAsync(owner, repo, branch, newCommitSha, ct);
                if (string.Equals(observed, newCommitSha, StringComparison.OrdinalIgnoreCase)) return;
                throw new GiteaWriteConflictException(
                    $"Branch HEAD conflict for {branch}: expectedHeadSha changed while applying the commit. Re-read before retrying.");
            }
            catch (Exception retryError) when (IsTransientWriteFailure(retryError, ct))
            {
                observed = await GetBranchHeadForRecoveryAsync(owner, repo, branch, newCommitSha, ct);
                if (string.Equals(observed, newCommitSha, StringComparison.OrdinalIgnoreCase)) return;
                throw new GiteaWriteOutcomeUnknownException(
                    $"Branch update outcome is unknown for {branch}. Its observed HEAD is {observed}; inspect the branch and diff before retrying.");
            }
        }
    }

    private async Task PutBranchRefAsync(
        string owner, string repo, string branch, string expectedHeadSha, string newCommitSha,
        CancellationToken ct)
    {
        var url = $"{RepoApiPath(owner, repo)}/branches/{Uri.EscapeDataString(branch)}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(new
            {
                new_commit_id = newCommitSha,
                old_commit_id = expectedHeadSha,
                force = false,
            }, options: JsonOpts),
        };
        using var response = await _writeHttp.SendAsync(request, ct);
        EnsureWriteSuccess(response, url, "update branch reference");
    }

    private async Task<string> GetBranchHeadForRecoveryAsync(
        string owner, string repo, string branch, string newCommitSha, CancellationToken ct)
    {
        try
        {
            var result = await GetAsync<GiteaBranch>(
                $"{RepoApiPath(owner, repo)}/branches/{Uri.EscapeDataString(branch)}", ct);
            var sha = result.Commit?.Id;
            if (!string.IsNullOrWhiteSpace(sha)) return sha;
        }
        catch (Exception)
        {
            throw new GiteaWriteOutcomeUnknownException(
                $"Could not verify whether branch {branch} advanced to {newCommitSha}. Inspect the branch and diff before retrying.");
        }

        throw new GiteaWriteOutcomeUnknownException(
            $"Could not verify whether branch {branch} advanced to {newCommitSha}. Inspect the branch and diff before retrying.");
    }

    private async Task<string?> TryDeleteStagingBranchAsync(
        string owner, string repo, string stagingBranch, CancellationToken ct)
    {
        var url = $"{RepoApiPath(owner, repo)}/branches/{Uri.EscapeDataString(stagingBranch)}";
        try
        {
            using var response = await _writeHttp.DeleteAsync(url, ct);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return null;
            return $"Temporary branch '{stagingBranch}' cleanup failed with HTTP {(int)response.StatusCode}; remove it after verifying it is an MCP staging ref.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"Temporary branch '{stagingBranch}' cleanup outcome is unknown; inspect it before removing it.";
        }
    }

    private static string AddCleanupWarning(string message, string? warning) =>
        string.IsNullOrWhiteSpace(warning) ? message : $"{message} {warning}";

    private static bool IsTransientWriteFailure(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is not GiteaWriteConflictException &&
        (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException);

    private async Task<GiteaContentMetadata?> GetFileMetadataAsync(
        string owner, string repo, string branch, string path, CancellationToken ct)
    {
        var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var url = $"{RepoApiPath(owner, repo)}/contents-ext/{encodedPath}?ref={Uri.EscapeDataString(branch)}";
        try
        {
            var response = await GetAsync<GiteaContentMetadataResponse>(url, ct);
            return response.FileContents;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private void CheckContentSize(string path, int byteCount)
    {
        if (byteCount > _opts.MaxWriteFileBytes)
            throw new ArgumentException(
                $"File {path} is {byteCount} UTF-8 bytes; the per-file limit is {_opts.MaxWriteFileBytes} bytes.");
    }

    private static string EncodeText(string content) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

    private static bool IsBlobSha(string sha) =>
        (sha.Length == 40 || sha.Length == 64) && sha.All(Uri.IsHexDigit);

    private static string RepoApiPath(string owner, string repo) =>
        $"/api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    private async Task<T> PostWriteAsync<T>(
        string relativeUrl, object payload, string operation, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = JsonContent.Create(payload, options: JsonOpts),
        };
        using var response = await _writeHttp.SendAsync(request, ct);
        EnsureWriteSuccess(response, relativeUrl, operation);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOpts)
            ?? throw new InvalidOperationException($"Gitea returned an empty response while attempting to {operation}.");
    }

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        var response = await SendAsync(relativeUrl, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOpts)
            ?? throw new InvalidOperationException($"Empty response from Gitea: {relativeUrl}");
    }

    private async Task<HttpResponseMessage> SendAsync(string relativeUrl, CancellationToken ct)
    {
        _logger.LogDebug("Gitea GET {Url}", relativeUrl);
        var response = await _http.GetAsync(relativeUrl, ct);
        await EnsureSuccessAsync(response, relativeUrl);
        return response;
    }

    /// <summary>
    /// 把 Gitea HTTP 错误码转换为语义清晰的异常。
    /// 绝不 swallow 异常——Claude 需要看到真实错误原因。
    /// </summary>
    private static Task EnsureSuccessAsync(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => new UnauthorizedAccessException(
                    $"Gitea read token is invalid or lacks permission. URL={url}, Status={response.StatusCode}."),
            HttpStatusCode.NotFound
                => new KeyNotFoundException($"Gitea repository or resource not found: {url}."),
            >= HttpStatusCode.InternalServerError
                => new InvalidOperationException(
                    $"Gitea backend error ({(int)response.StatusCode}). URL={url}."),
            _ => new HttpRequestException(
                    $"Gitea returned {(int)response.StatusCode}. URL={url}.")
        };
    }

    private static void EnsureWriteSuccess(HttpResponseMessage response, string url, string operation)
    {
        if (response.IsSuccessStatusCode) return;

        throw response.StatusCode switch
        {
            HttpStatusCode.BadRequest
                => new ArgumentException($"Gitea rejected {operation}. Check the workflow/ref/inputs or operation parameters."),
            HttpStatusCode.Unauthorized
                => new UnauthorizedAccessException("Gitea write token is invalid or expired."),
            HttpStatusCode.Forbidden
                => new UnauthorizedAccessException("Gitea write token lacks permission for this repository or operation."),
            HttpStatusCode.NotFound
                => new KeyNotFoundException($"Gitea repository, source ref, or target branch was not found while attempting to {operation}."),
            HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity
                => new GiteaWriteConflictException(operation switch
                {
                    "create branch" => "The branch already exists or the source ref is invalid. Check both values and retry with a new branch name.",
                    "update branch reference" => "Branch HEAD conflict: Gitea's old_commit_id check failed, so the target branch was not updated. Re-read the working branch and rebuild the changes.",
                    "create pull request" => "A pull request for this head/base may already exist, or the PR request is invalid. Check list_pulls/read_pull before retrying.",
                    "merge pull request" => "Gitea rejected the merge: PR head changed, conflicts exist, or repository merge requirements are not satisfied. Read the PR and checks again.",
                    "dispatch workflow" => "Gitea rejected the dispatch. Check workflow_dispatch support, ref and required inputs.",
                    "rerun workflow" => "Gitea rejected the rerun. Check whether the run exists, is still active, or can be rerun.",
                    _ => $"Gitea rejected the {operation} because a file SHA changed, the branch already exists, or the request is invalid. Re-read current refs and file SHAs before retrying.",
                }),
            (HttpStatusCode)423
                => new UnauthorizedAccessException("Gitea rejected the write because the repository is archived."),
            _ => new HttpRequestException(
                $"Gitea {operation} failed with HTTP {(int)response.StatusCode}. No response body was included to avoid exposing repository content.")
        };
    }
}

public sealed class GiteaWriteConflictException(string message) : InvalidOperationException(message);

public sealed class GiteaWriteOutcomeUnknownException(string message) : InvalidOperationException(message);
