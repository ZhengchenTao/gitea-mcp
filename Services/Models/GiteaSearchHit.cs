using System.Text.Json.Serialization;

namespace GiteaMcp.Services.Models;

/// <summary>代码搜索结果条目（search_code 使用）</summary>
public class GiteaSearchHit
{
    /// <summary>仓库所有者</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>仓库名称</summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>命中的文件路径（相对仓库根）</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>命中的行号（1-based，Gitea 索引未启用时为 0）</summary>
    public int Line { get; set; }

    /// <summary>命中行的前后上下文预览（Gitea 返回原始片段）</summary>
    public string Preview { get; set; } = string.Empty;
}

/// <summary>Gitea code search API 返回的单条结果</summary>
public class GiteaCodeSearchResult
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("commit_id")]
    public string? CommitId { get; set; }

    [JsonPropertyName("repo")]
    public GiteaRepo? Repo { get; set; }
}

public class GiteaCodeSearchResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("data")]
    public List<GiteaCodeSearchResult> Data { get; set; } = [];
}
