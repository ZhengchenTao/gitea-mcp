using System.Text.Json.Serialization;

namespace GiteaMcp.Services.Models;

public class GiteaOrg
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("website")]
    public string? Website { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("repo_admin_change_team_access")]
    public bool RepoAdminChangeTeamAccess { get; set; }

    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }
}

/// <summary>/api/v1/orgs/search 的响应包装（顶层 { ok, data }）。</summary>
public class GiteaOrgSearchResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("data")]
    public List<GiteaOrg> Data { get; set; } = [];
}
