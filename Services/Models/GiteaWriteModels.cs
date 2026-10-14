using System.ComponentModel;
using System.Text.Json.Serialization;

namespace GiteaMcp.Services.Models;

/// <summary>One file operation accepted by commit_files.</summary>
public sealed class GiteaWriteFileChange
{
    [Description("Operation: create, update, or delete. Update/delete require expectedSha.")]
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [Description("Relative repository path, such as data/room.yaml. Absolute paths and traversal segments are rejected.")]
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [Description("UTF-8 text for create/update. Omit for delete.")]
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [Description("Expected current Git blob SHA; required for update/delete and must be omitted for create.")]
    [JsonPropertyName("expectedSha")]
    public string? ExpectedSha { get; set; }
}

public sealed class GiteaContentMetadataResponse
{
    [JsonPropertyName("file_contents")]
    public GiteaContentMetadata? FileContents { get; set; }
}

public sealed class GiteaContentMetadata
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

public sealed class GiteaFilesResponse
{
    [JsonPropertyName("commit")]
    public GiteaWriteCommit? Commit { get; set; }

    [JsonIgnore]
    public string? CleanupWarning { get; set; }
}

public sealed class GiteaWriteCommit
{
    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("parents")]
    public List<GiteaWriteCommitParent> Parents { get; set; } = [];
}

public sealed class GiteaWriteCommitParent
{
    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;
}
