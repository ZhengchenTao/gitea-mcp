namespace GiteaMcp.Config;

/// <summary>
/// /.well-known/oauth-authorization-server + /.well-known/oauth-protected-resource
/// 端点返回的元数据，字段由 Mcp:OAuthDiscovery:* 配置项驱动。
/// </summary>
public class McpDiscoveryOptions
{
    public const string SectionName = "Mcp:OAuthDiscovery";

    public string Issuer { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string RegistrationEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// 本资源服务的标识符（RFC 9728 PRM 的 `resource` 字段，必须与 auth server
    /// 上该资源条目的 resource_url 完全一致）。
    /// 留空时 PRM 端点回退用请求的 `scheme://host`。
    /// </summary>
    public string ResourceUrl { get; set; } = string.Empty;
}
