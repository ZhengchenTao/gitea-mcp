using System.Text.Json.Serialization;

namespace GiteaMcp.Services.Models;

/// <summary>Gitea commit 摘要（list_commits 使用）</summary>
public class GiteaCommitSummary
{
    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; }

    [JsonPropertyName("commit")]
    public GiteaCommitDetail? Commit { get; set; }

    [JsonPropertyName("author")]
    public GiteaUser? Author { get; set; }
}

public class GiteaCommitDetail
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public GiteaCommitSignature? Author { get; set; }

    [JsonPropertyName("committer")]
    public GiteaCommitSignature? Committer { get; set; }
}

public class GiteaCommitSignature
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }
}

/// <summary>read_commit 使用的完整 commit（含 diff 文件列表）</summary>
public class GiteaCommitFull
{
    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; }

    [JsonPropertyName("commit")]
    public GiteaCommitDetail? Commit { get; set; }

    [JsonPropertyName("author")]
    public GiteaUser? Author { get; set; }

    [JsonPropertyName("files")]
    public List<GiteaCommitFile>? Files { get; set; }

    [JsonPropertyName("stats")]
    public GiteaCommitStats? Stats { get; set; }
}

public class GiteaCommitFile
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

    [JsonPropertyName("patch")]
    public string? Patch { get; set; }
}

public class GiteaCommitStats
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("additions")]
    public int Additions { get; set; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; set; }
}
