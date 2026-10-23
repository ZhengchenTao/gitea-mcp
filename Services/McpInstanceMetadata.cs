using GiteaMcp.Config;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GiteaMcp.Services;

/// <summary>统一生成握手说明及工具描述，所有实例地址均来自后端配置。</summary>
public sealed class McpInstanceMetadata
{
    private readonly string _instanceName;
    private readonly string _baseUrl;
    private readonly string _usageDescription;

    public McpInstanceMetadata(string baseUrl, McpInstanceOptions options)
    {
        // 这些内容会发给客户端；拒绝可能携带凭据的 URL，错误中不复述配置值。
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                "Gitea:BaseUrl must be an absolute HTTP(S) URL without credentials, query or fragment.");

        _baseUrl = uri.AbsoluteUri.TrimEnd('/');
        _instanceName = string.IsNullOrWhiteSpace(options.InstanceName)
            ? $"Gitea at {uri.Authority}"
            : options.InstanceName.Trim();
        _usageDescription = (options.UsageDescription ?? "").Trim();
    }

    public void Configure(McpServerOptions options)
    {
        var identity = $"Gitea instance: {_instanceName} ({_baseUrl}).";
        var description = string.IsNullOrEmpty(_usageDescription)
            ? identity
            : $"{identity} {_usageDescription}";
        options.ServerInfo = new Implementation
        {
            Name = "gitea-mcp",
            Version = typeof(McpInstanceMetadata).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            Title = _instanceName,
            Description = description,
        };
        options.ServerInstructions = description;

        // 只提供实例身份；所有用途、路由、Git/PR 策略均由部署方的 UsageDescription 决定。
        // PostConfigure 在 SDK 收集完全部工具后执行，保留名称、schema、annotations 和调用实现。
        var prefix = description + " ";
        if (options.ToolCollection is not null)
            foreach (var tool in options.ToolCollection)
                if (!(tool.ProtocolTool.Description ?? "").StartsWith(prefix, StringComparison.Ordinal))
                    tool.ProtocolTool.Description = prefix + tool.ProtocolTool.Description;
    }
}
