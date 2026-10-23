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
    public void Instructions_IdentifyConfiguredInstanceAndRoutingBoundary()
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
        Assert.Contains("ask for clarification before searching or writing", options.ServerInstructions);
        Assert.Contains("Never send another instance's files", options.ServerInstructions);
        Assert.Contains("Prefer local Git", options.ServerInstructions);
        Assert.Contains("Personal repositories only; not the company instance.", options.ServerInstructions);
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
        });

        metadata.Configure(options);
        metadata.Configure(options); // 不在重复配置时累加描述。

        Assert.Equal(original.Count, options.ToolCollection.Count);
        foreach (var tool in options.ToolCollection)
        {
            var before = original[tool.ProtocolTool.Name];
            var originalDescription = before["description"]!.GetValue<string>();
            var description = tool.ProtocolTool.Description!;
            Assert.StartsWith("Company Gitea (https://company.example.com) only. ", description);
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
        Assert.Contains("without a local checkout", options.ServerInstructions);
        Assert.All(options.ToolCollection!, tool =>
            Assert.StartsWith("Gitea at git.example.com:8443 (https://git.example.com:8443) only.",
                tool.ProtocolTool.Description));
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
