using GiteaMcp.Config;
using GiteaMcp.Services.Models;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GiteaMcp.Services;

/// <summary>
/// Gitea REST API 封装。
/// 使用 HttpClientFactory 注入（named client "gitea"），统一带 admin PAT header。
/// 所有公开方法在 Gitea 返回错误时抛出结构化异常，不会静默返回空值。
/// </summary>
public class GiteaApiClient
{
    private readonly HttpClient _http;
    private readonly GiteaOptions _opts;
    private readonly ILogger<GiteaApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public GiteaApiClient(
        IHttpClientFactory factory,
        IOptions<GiteaOptions> opts,
        ILogger<GiteaApiClient> logger)
    {
        _opts = opts.Value;
        _logger = logger;
        _http = factory.CreateClient("gitea");
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
            url += $"&q={Uri.EscapeDataString(owner)}";

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
    /// 列出所有组织。优先 /orgs/search（任意 read PAT 即可）；
    /// 仅当带 admin scope 的 PAT 才能访问 /admin/orgs。本服务的 PAT 设计为只读，
    /// 因此不走 /admin/* 端点。/orgs/search 不传 q 时返回所有可见 org。
    /// </summary>
    public async Task<List<GiteaOrg>> GetOrgsAsync(int limit, CancellationToken ct = default)
    {
        // /orgs/search 返回 { ok, data: [...] }
        var resp = await GetAsync<GiteaOrgSearchResult>($"/api/v1/orgs/search?limit={limit}", ct);
        return resp.Data;
    }

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
    /// 获取 run/job 日志。Gitea Actions log API 返回 ZIP 或纯文本，
    /// 这里最多读 maxBytes（1MB）防爆内存。
    /// </summary>
    public async Task<string> GetRunLogAsync(
        string owner, string repo, long runId, long? jobId, int maxBytes = 1024 * 1024,
        CancellationToken ct = default)
    {
        var url = jobId.HasValue
            ? $"/api/v1/repos/{owner}/{repo}/actions/runs/{runId}/jobs/{jobId}/logs"
            : $"/api/v1/repos/{owner}/{repo}/actions/runs/{runId}/logs";

        try
        {
            var response = await SendAsync(url, ct);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[maxBytes];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(), ct);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
            return bytesRead == maxBytes
                ? text + "\n[...log truncated at 1MB...]"
                : text;
        }
        catch (KeyNotFoundException)
        {
            // Actions log 可能还未生成（run 刚开始）
            return "[Log not yet available]";
        }
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
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode) return;

        var body = string.Empty;
        try { body = await response.Content.ReadAsStringAsync(); } catch { }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => new UnauthorizedAccessException(
                    $"Gitea PAT invalid or insufficient scope. URL={url}, Status={response.StatusCode}, Body={body}"),
            HttpStatusCode.NotFound
                => new KeyNotFoundException(
                    $"Repo/Resource not found: {url}. Body={body}"),
            >= HttpStatusCode.InternalServerError
                => new InvalidOperationException(
                    $"Gitea backend error ({(int)response.StatusCode}). URL={url}, Body={body}"),
            _ => new HttpRequestException(
                    $"Gitea returned {(int)response.StatusCode}. URL={url}, Body={body}")
        };
    }
}
