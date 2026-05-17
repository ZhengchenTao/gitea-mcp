using System.Text.Json.Serialization;

namespace GiteaMcp.Services.Models;

public class GiteaWorkflowRun
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("head_branch")]
    public string? HeadBranch { get; set; }

    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; set; }

    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("workflow_id")]
    public long WorkflowId { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("actor")]
    public GiteaUser? Actor { get; set; }
}

public class GiteaWorkflowRunList
{
    [JsonPropertyName("workflow_runs")]
    public List<GiteaWorkflowRun> WorkflowRuns { get; set; } = [];

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
}

public class GiteaWorkflowJob
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }
}

public class GiteaWorkflowJobList
{
    [JsonPropertyName("workflow_jobs")]
    public List<GiteaWorkflowJob> WorkflowJobs { get; set; } = [];

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
}
