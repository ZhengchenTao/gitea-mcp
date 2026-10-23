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
        options.ServerInfo = new Implementation
        {
            Name = "gitea-mcp",
            Version = typeof(McpInstanceMetadata).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            Title = _instanceName,
            Description = $"Access only {_instanceName} ({_baseUrl}). {_usageDescription}",
        };
        options.ServerInstructions =
            $"This server connects only to {_instanceName} ({_baseUrl}). " +
            "Every owner/repo, organization, branch, issue, pull request and workflow belongs to this instance; " +
            "tools cannot switch to another Gitea, GitLab or GitHub server. " +
            "Confirm the target instance from a repository URL, Git remote, or the user's explicit selection of this instance before using these tools. " +
            "A matching owner/repo name on another server is not evidence of a match. " +
            "If the target instance is unclear, ask for clarification before searching or writing here. " +
            "Never send another instance's files, code or changes here as a fallback. " +
            "Prefer local Git and repository tooling when a usable local checkout exists, unless the user requests MCP. " +
            "Follow the target repository's instructions; create a branch or pull request only when the task, " +
            "repository rules or branch protection requires it. " + _usageDescription;

        // PostConfigure 在 SDK 收集完全部工具后执行，统一覆盖现有和未来新增工具。
        // 只补描述；工具名、参数 schema、annotations 与调用实现均保留。
        var prefix = $"{_instanceName} ({_baseUrl}) only. " +
            "Confirm the instance by URL, Git remote or explicit user selection, not owner/repo alone; " +
            "if unclear, ask before calling. Prefer local Git when available. ";
        if (options.ToolCollection is not null)
            foreach (var tool in options.ToolCollection)
                if (!(tool.ProtocolTool.Description ?? "").StartsWith(prefix, StringComparison.Ordinal))
                    tool.ProtocolTool.Description = prefix + tool.ProtocolTool.Description;
    }
}
