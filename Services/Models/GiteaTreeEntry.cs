using System.Text.Json.Serialization;

namespace GiteaMcp.Services.Models;

/// <summary>Git tree 中的单个条目（list_tree 使用）</summary>
public class GiteaTreeEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>"blob"（文件）/ "tree"（目录）/ "commit"（子模块）</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public class GiteaTree
{
    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("tree")]
    public List<GiteaTreeEntry> Tree { get; set; } = [];

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }
}

/// <summary>分支信息（list_branches 使用）</summary>
public class GiteaBranch
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("commit")]
    public GiteaBranchCommit? Commit { get; set; }

    [JsonPropertyName("protected")]
    public bool Protected { get; set; }
}

public class GiteaBranchCommit
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("author")]
    public GiteaCommitSignature? Author { get; set; }
}
