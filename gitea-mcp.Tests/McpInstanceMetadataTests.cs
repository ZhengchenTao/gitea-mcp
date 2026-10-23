using GiteaMcp.Config;
using GiteaMcp.Services;
using GiteaMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.Text.Json;
using Xunit;

namespace GiteaMcp.Tests;

public sealed class McpInstanceMetadataTests
{
    [Fact]
    public void Instructions_IncludeOnlyInstanceIdentityAndConfiguredPolicy()
    {
        var options = new McpServerOptions();
        new McpInstanceMetadata("https://git.example.com/gitea/", new()
        {
            InstanceName = "Personal Gitea",
            UsageDescription = "Personal repositories only; not the company instance.",
        }).Configure(options);

        Assert.Equal("gitea-mcp", options.ServerInfo!.Name);
        Assert.Equal("Personal Gitea", options.ServerInfo.Title);
        Assert.Contains("https://git.example.com/gitea", options.ServerInstructions);
        Assert.Equal(
            "Gitea instance: Personal Gitea (https://git.example.com/gitea). " +
            "Personal repositories only; not the company instance.", options.ServerInstructions);
        Assert.Equal(options.ServerInstructions, options.ServerInfo.Description);
    }

    [Fact]
    public void SdkRegistration_DecoratesEveryToolWithoutChangingSchemasOrAnnotations()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer().WithToolsFromAssembly(typeof(RepoTools).Assembly);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        Assert.NotNull(options.ToolCollection);
        Assert.True(options.ToolCollection.Count > 20);
        var original = options.ToolCollection.ToDictionary(t => t.ProtocolTool.Name,
            t => JsonSerializer.SerializeToNode(t.ProtocolTool)!.AsObject());
        var metadata = new McpInstanceMetadata("https://company.example.com/", new()
        {
            InstanceName = "Company Gitea",
            UsageDescription = "Prefer MCP even when local Git is available; follow the team PR workflow.",
        });

        metadata.Configure(options);
        metadata.Configure(options); // 不在重复配置时累加描述。

        Assert.Equal(original.Count, options.ToolCollection.Count);
        foreach (var tool in options.ToolCollection)
        {
            var before = original[tool.ProtocolTool.Name];
            var originalDescription = before["description"]!.GetValue<string>();
            var description = tool.ProtocolTool.Description!;
            Assert.StartsWith("Gitea instance: Company Gitea (https://company.example.com). " +
                "Prefer MCP even when local Git is available; follow the team PR workflow. ", description);
            Assert.EndsWith(originalDescription, description);
            Assert.Equal(1, description.Split("Company Gitea (").Length - 1);
            var after = JsonSerializer.SerializeToNode(tool.ProtocolTool)!.AsObject();
            after["description"] = before["description"]!.DeepClone();
            Assert.Equal(before.ToJsonString(), after.ToJsonString());
        }
    }

    [Fact]
    public void PostConfigure_UsesInstanceIdentityInResolvedSdkOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer().WithToolsFromAssembly(typeof(RepoTools).Assembly);
        services.PostConfigure<McpServerOptions>(
            new McpInstanceMetadata("https://git.example.com:8443", new()).Configure);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        Assert.Equal("Gitea at git.example.com:8443", options.ServerInfo!.Title);
        Assert.Equal("Gitea instance: Gitea at git.example.com:8443 (https://git.example.com:8443).",
            options.ServerInstructions);
        Assert.All(options.ToolCollection!, tool =>
            Assert.StartsWith("Gitea instance: Gitea at git.example.com:8443 (https://git.example.com:8443). ",
                tool.ProtocolTool.Description));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyPolicy_AdvertisesIdentityWithoutUsageOrRoutingRules(string? policy)
    {
        var options = new McpServerOptions();
        new McpInstanceMetadata("https://git.example.com", new()
        {
            UsageDescription = policy!,
        }).Configure(options);

        Assert.Equal("Gitea instance: Gitea at git.example.com (https://git.example.com).",
            options.ServerInstructions);
        Assert.Equal(options.ServerInstructions, options.ServerInfo!.Description);
    }

    [Theory]
    [InlineData("https://user:do-not-expose@git.example.com")]
    [InlineData("https://git.example.com?token=do-not-expose")]
    [InlineData("https://git.example.com/#do-not-expose")]
    [InlineData("file:///do-not-expose")]
    [InlineData("do-not-expose")]
    public void RejectsUnsafeIdentityUrlsWithoutEchoingTheirValues(string url)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new McpInstanceMetadata(url, new()));
        Assert.DoesNotContain("do-not-expose", error.Message);
    }
}
