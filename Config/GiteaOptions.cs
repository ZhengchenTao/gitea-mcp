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
    /// 黑名单：逗号分隔的 owner/repo，格式如 "alice/secret-repo,acme/internal"。
    /// 黑名单内的 repo 不会出现在任何 Tool 的返回值里。默认空（全开放）。
    /// </summary>
    public string RepoBlacklist { get; set; } = string.Empty;

    /// <summary>list_repos 的默认 limit（不传时使用）</summary>
    public int DefaultLimit { get; set; } = 50;

    /// <summary>read_file 的默认最大字节数（1MB）</summary>
    public int MaxFileBytes { get; set; } = 1 * 1024 * 1024;
}
