using GiteaMcp.Config;
using GiteaMcp.Services.Models;
using Microsoft.Extensions.Options;

namespace GiteaMcp.Services;

/// <summary>
/// 仓库黑名单过滤器。
/// 配置项 Gitea:RepoBlacklist 为逗号分隔的 "owner/repo" 列表，默认空（全开放）。
/// 所有返回仓库列表的 Tool 都经过此过滤器，防止意外暴露不希望 Claude 看到的仓库。
/// </summary>
public class GiteaRepoFilter
{
    private readonly HashSet<string> _blacklist;

    public GiteaRepoFilter(IOptions<GiteaOptions> opts)
    {
        var raw = opts.Value.RepoBlacklist ?? string.Empty;

        // 解析 "owner/repo,owner2/repo2" 格式，大小写不敏感
        _blacklist = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();
    }

    /// <summary>
    /// 判断单个仓库是否在黑名单内。
    /// full_name 格式为 "owner/repo"（Gitea API 返回的 full_name 字段）。
    /// </summary>
    public bool IsBlocked(string fullName)
        => _blacklist.Contains(fullName.ToLowerInvariant());

    /// <summary>
    /// 从列表中过滤掉黑名单仓库，返回新列表（不修改原列表）。
    /// </summary>
    public List<GiteaRepo> Filter(List<GiteaRepo> repos)
    {
        if (_blacklist.Count == 0) return repos;
        return repos.Where(r => !IsBlocked(r.FullName)).ToList();
    }

    /// <summary>当前黑名单项数，用于日志 / 测试断言</summary>
    public int BlacklistCount => _blacklist.Count;
}
