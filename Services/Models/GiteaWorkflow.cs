using System.Text.Json.Serialization;

namespace GiteaMcp.Services.Models;

public class GiteaWorkflow
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("path")]
    public string? Path { get; set; }
    [JsonPropertyName("state")]
    public string? State { get; set; }
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

public class GiteaWorkflowList
{
    [JsonPropertyName("workflows")]
    public List<GiteaWorkflow> Workflows { get; set; } = [];
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
}

public class GiteaWorkflowDispatchResult
{
    [JsonPropertyName("workflow_run_id")]
    public long? RunId { get; set; }
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
    [JsonPropertyName("run_url")]
    public string? RunUrl { get; set; }
}
