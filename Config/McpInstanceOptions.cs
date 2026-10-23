namespace GiteaMcp.Config;

/// <summary>向 AI 客户端说明当前实例的身份与用途；这里只放可公开的描述。</summary>
public sealed class McpInstanceOptions
{
    public const string SectionName = "Mcp";

    /// <summary>例如 Personal Gitea / Company Gitea；留空时使用实例主机名。</summary>
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>可选的用途与调用策略，附加到初始化说明和每个工具描述；默认无策略，不用于授权。</summary>
    public string UsageDescription { get; set; } = string.Empty;
}
