using System.Text.Json.Serialization;

namespace GiteaMcp.Services.Models;

public class GiteaPullRequest
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public GiteaUser? User { get; set; }

    [JsonPropertyName("head")]
    public GiteaPrRef? Head { get; set; }

    [JsonPropertyName("base")]
    public GiteaPrRef? Base { get; set; }

    [JsonPropertyName("merged")]
    public bool Merged { get; set; }

    [JsonPropertyName("mergeable")]
    public bool? Mergeable { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("closed_at")]
    public DateTimeOffset? ClosedAt { get; set; }

    [JsonPropertyName("merged_at")]
    public DateTimeOffset? MergedAt { get; set; }

    [JsonPropertyName("labels")]
    public List<GiteaLabel>? Labels { get; set; }
}

public class GiteaPrRef
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("repo")]
    public GiteaRepo? Repo { get; set; }
}

/// <summary>PR 变更文件（read_pull 附带返回）</summary>
public class GiteaPrFile
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("additions")]
    public int Additions { get; set; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; set; }

    [JsonPropertyName("changes")]
    public int Changes { get; set; }
}
