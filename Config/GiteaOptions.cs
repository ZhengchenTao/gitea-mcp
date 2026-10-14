namespace GiteaMcp.Config;

/// <summary>
/// Gitea 后端连接配置，通过 env / appsettings 注入。
/// 生产环境敏感字段（AdminPat）必须通过环境变量注入，不要写进代码。
/// </summary>
public class GiteaOptions
{
    public const string SectionName = "Gitea";

    /// <summary>Gitea 根 URL（末尾无斜杠），必须通过 env Gitea__BaseUrl 注入</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gitea Admin PAT（只读权限：read:repository / read:issue / read:user / read:organization / read:package）。
    /// 生产环境从 env Gitea__AdminPat 注入，本地开发用 dotnet user-secrets。
    /// 绝对不要 hardcode。
    /// </summary>
    public string AdminPat { get; set; } = string.Empty;

    /// <summary>
    /// 独立的 Gitea 写入 PAT。留空时全部写入工具关闭；仓库访问范围由该令牌所属账号权限决定。
    /// 环境变量：Gitea__WritePat。
    /// </summary>
    public string WritePat { get; set; } = string.Empty;

    /// <summary>
    /// 黑名单：逗号分隔的 owner/repo，格式如 "alice/secret-repo,acme/internal"。
    /// 黑名单内的 repo 不会出现在任何 Tool 的返回值里。默认空（全开放）。
    /// </summary>
    public string RepoBlacklist { get; set; } = string.Empty;

    /// <summary>额外禁止直接提交的分支名。默认空，允许 main/默认分支；Gitea 自身保护规则仍然生效。</summary>
    public string WriteProtectedBranches { get; set; } = string.Empty;

    /// <summary>list_repos 的默认 limit（不传时使用）</summary>
    public int DefaultLimit { get; set; } = 50;

    /// <summary>read_file 的默认最大字节数（1MB）</summary>
    public int MaxFileBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>一次写入允许的最大文件数。</summary>
    public int MaxWriteFiles { get; set; } = 20;

    /// <summary>单个写入文件的最大 UTF-8 字节数。</summary>
    public int MaxWriteFileBytes { get; set; } = 256 * 1024;

    /// <summary>一次多文件提交的最大 UTF-8 文件内容总字节数。</summary>
    public int MaxWriteRequestBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>MCP HTTP 请求体上限；工具仍另行限制文件内容总量和单文件大小。</summary>
    public long MaxMcpRequestBodyBytes { get; set; } = 8 * 1024 * 1024;
}
