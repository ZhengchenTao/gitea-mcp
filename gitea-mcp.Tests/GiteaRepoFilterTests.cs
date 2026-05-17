using GiteaMcp.Config;
using GiteaMcp.Services;
using GiteaMcp.Services.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace GiteaMcp.Tests;

/// <summary>
/// GiteaRepoFilter 单元测试。
/// 验证黑名单过滤的核心行为：精确匹配、大小写不敏感、空黑名单全通过。
/// </summary>
public class GiteaRepoFilterTests
{
    private static GiteaRepoFilter CreateFilter(string blacklist) =>
        new(Options.Create(new GiteaOptions { RepoBlacklist = blacklist }));

    private static GiteaRepo MakeRepo(string owner, string name) => new()
    {
        Name = name,
        FullName = $"{owner}/{name}",
        Owner = new GiteaUser { Login = owner },
    };

    [Fact]
    public void EmptyBlacklist_AllowsEverything()
    {
        var filter = CreateFilter("");
        var repos = new List<GiteaRepo>
        {
            MakeRepo("alice", "repo-a"),
            MakeRepo("org", "private-repo"),
        };

        var result = filter.Filter(repos);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, filter.BlacklistCount);
    }

    [Fact]
    public void SingleEntry_BlocksExactMatch()
    {
        var filter = CreateFilter("alice/secret-repo");
        var repos = new List<GiteaRepo>
        {
            MakeRepo("alice", "secret-repo"),
            MakeRepo("alice", "public-repo"),
        };

        var result = filter.Filter(repos);

        Assert.Single(result);
        Assert.Equal("public-repo", result[0].Name);
    }

    [Fact]
    public void MultipleEntries_CommaSeparated()
    {
        var filter = CreateFilter("alice/repo-a, org/internal , bob/secret");
        var repos = new List<GiteaRepo>
        {
            MakeRepo("alice", "repo-a"),
            MakeRepo("org", "internal"),
            MakeRepo("bob", "secret"),
            MakeRepo("bob", "public"),
        };

        var result = filter.Filter(repos);

        Assert.Single(result);
        Assert.Equal("public", result[0].Name);
    }

    [Fact]
    public void IsBlocked_CaseInsensitive()
    {
        var filter = CreateFilter("Alice/Secret-Repo");

        // 大写 owner / 混合大小写 repo 都应该被屏蔽
        Assert.True(filter.IsBlocked("alice/secret-repo"));
        Assert.True(filter.IsBlocked("ALICE/SECRET-REPO"));
        Assert.False(filter.IsBlocked("alice/other-repo"));
    }

    [Fact]
    public void Filter_DoesNotMutateOriginalList()
    {
        var filter = CreateFilter("alice/repo-a");
        var original = new List<GiteaRepo>
        {
            MakeRepo("alice", "repo-a"),
            MakeRepo("alice", "repo-b"),
        };
        var originalCount = original.Count;

        var filtered = filter.Filter(original);

        // 原列表不应被修改
        Assert.Equal(originalCount, original.Count);
        Assert.Single(filtered);
    }

    [Fact]
    public void IsBlocked_ReturnsFalse_ForNonBlockedRepo()
    {
        var filter = CreateFilter("alice/secret");
        Assert.False(filter.IsBlocked("alice/public"));
        Assert.False(filter.IsBlocked("bob/anything"));
    }
}
