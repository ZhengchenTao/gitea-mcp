using System.Text.Json.Serialization;

namespace GiteaMcp.Services.Models;

public class GiteaPackage
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("owner")]
    public GiteaUser? Owner { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("creator")]
    public GiteaUser? Creator { get; set; }

    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; }
}
