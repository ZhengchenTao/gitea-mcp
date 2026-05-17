using System.Text.Json.Serialization;

namespace GiteaMcp.Services.Models;

/// <summary>Gitea 仓库元数据（list_repos / read_repo 使用）</summary>
public class GiteaRepo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("fork")]
    public bool Fork { get; set; }

    [JsonPropertyName("mirror")]
    public bool Mirror { get; set; }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("clone_url")]
    public string CloneUrl { get; set; } = string.Empty;

    [JsonPropertyName("default_branch")]
    public string DefaultBranch { get; set; } = "main";

    [JsonPropertyName("stars_count")]
    public int StarsCount { get; set; }

    [JsonPropertyName("forks_count")]
    public int ForksCount { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("website")]
    public string? Website { get; set; }

    [JsonPropertyName("topics")]
    public List<string>? Topics { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("owner")]
    public GiteaUser? Owner { get; set; }
}

public class GiteaUser
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }
}

/// <summary>搜索仓库的 API 响应包装</summary>
public class GiteaRepoSearchResult
{
    [JsonPropertyName("data")]
    public List<GiteaRepo> Data { get; set; } = [];

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
}
