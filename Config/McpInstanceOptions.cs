namespace GiteaMcp.Config;

/// <summary>向 AI 客户端说明当前实例的身份与用途；这里只放可公开的描述。</summary>
public sealed class McpInstanceOptions
{
    public const string SectionName = "Mcp";

    /// <summary>例如 Personal Gitea / Company Gitea；留空时使用实例主机名。</summary>
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>部署方补充的用途说明，不用于授权，也不能替代实例匹配。</summary>
    public string UsageDescription { get; set; } =
        "Primarily for AI clients without a local checkout or working environment, such as chat-based assistants.";
}
