using System.Text.Json.Serialization;

namespace GiteaMcp.Services.Models;

public class GiteaIssue
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

    [JsonPropertyName("labels")]
    public List<GiteaLabel>? Labels { get; set; }

    [JsonPropertyName("assignees")]
    public List<GiteaUser>? Assignees { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("closed_at")]
    public DateTimeOffset? ClosedAt { get; set; }

    [JsonPropertyName("comments")]
    public int Comments { get; set; }
}

public class GiteaLabel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("color")]
    public string Color { get; set; } = string.Empty;
}

public class GiteaComment
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("user")]
    public GiteaUser? User { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}
