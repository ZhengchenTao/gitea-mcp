using GiteaMcp.Auth;
using GiteaMcp.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

namespace GiteaMcp.Services;

/// <summary>
/// Server-side write boundary. The MCP endpoint still requires read:gitea for compatibility;
/// every write API call additionally requires write:gitea and an exact repository allowlist entry.
/// </summary>
public sealed class GiteaWritePolicy
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex RepoSegmentPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled);
    private static readonly Regex CommitShaPattern = new("^[a-fA-F0-9]{7,64}$", RegexOptions.Compiled);
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly GiteaOptions _options;
    private readonly GiteaRepoFilter _repoFilter;
    private readonly HashSet<string> _writeAllowlist;
    private readonly HashSet<string> _protectedBranches;

    public GiteaWritePolicy(
        IHttpContextAccessor httpContextAccessor,
        IOptions<GiteaOptions> options,
        GiteaRepoFilter repoFilter)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options.Value;
        _repoFilter = repoFilter;
        _writeAllowlist = ParseSet(_options.WriteRepoAllowlist);
        _protectedBranches = ParseSet(_options.WriteProtectedBranches);
        _protectedBranches.Add("main");
        _protectedBranches.Add("master");
    }

    public void RequireWriteAccess(string owner, string repo)
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true || !HasScope(principal, ScopePolicies.WriteGitea))
            throw new UnauthorizedAccessException(
                "This Gitea write tool requires an access token with scope write:gitea. Reauthorize the MCP connection with that scope.");

        ValidateRepoSegment(owner, "owner");
        ValidateRepoSegment(repo, "repo");
        var fullName = $"{owner}/{repo}";

        if (_repoFilter.IsBlocked(fullName))
            throw new UnauthorizedAccessException($"Repository {fullName} is blocked by Gitea:RepoBlacklist.");

        if (_writeAllowlist.Count == 0 || !_writeAllowlist.Contains(fullName))
            throw new UnauthorizedAccessException(
                $"Repository {fullName} is not in the Gitea write allowlist.");

        if (string.IsNullOrWhiteSpace(_options.WritePat))
            throw new InvalidOperationException(
                "Gitea writes are disabled because Gitea:WritePat is not configured.");
    }

    public void ValidateNewBranchName(string branch)
    {
        ValidateBranchName(branch);
        if (IsProtectedBranchName(branch))
            throw new UnauthorizedAccessException(
                $"Creating or writing protected branch '{branch}' is disabled. Create a feature branch instead.");
    }

    public void ValidateWritableBranchName(string branch)
    {
        ValidateBranchName(branch);
        if (IsProtectedBranchName(branch))
            throw new UnauthorizedAccessException(
                $"Direct commits to protected branch '{branch}' are disabled. Create a working branch and open a pull request.");
    }

    public bool IsProtectedBranchName(string branch) =>
        _protectedBranches.Contains(branch);

    public static void ValidateBranchName(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.Length > 100 ||
            branch.StartsWith('/') || branch.EndsWith('/') || branch.Contains("//", StringComparison.Ordinal) ||
            branch.Contains("..", StringComparison.Ordinal) || branch.Contains("@{", StringComparison.Ordinal) ||
            branch.EndsWith('.') || branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            branch.Split('/').Any(part => part.Length == 0 || part.StartsWith('.')) ||
            branch.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
        {
            throw new ArgumentException("Branch name is invalid. Use a valid Git branch name.", nameof(branch));
        }
    }

    public static void ValidateRefName(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 100 ||
            reference.Any(char.IsControl) || reference.Any(char.IsWhiteSpace))
            throw new ArgumentException("Base ref must be a branch, tag, or commit SHA.", nameof(reference));

        if (!CommitShaPattern.IsMatch(reference))
            ValidateBranchName(reference);
    }

    public static void ValidateFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 500 || path.StartsWith('/') ||
            path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
            throw new ArgumentException("File path must be a relative repository path without traversal segments.", nameof(path));

        var segments = path.Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("File path must not contain empty, traversal, or reserved '.git' path segments.", nameof(path));

        // Reject encoded separators/traversal too. The path is later URL-escaped segment by segment;
        // this check prevents a proxy or Gitea router from interpreting an encoded path as a new segment.
        var decoded = path;
        for (var i = 0; i < 3; i++)
        {
            string next;
            try { next = Uri.UnescapeDataString(decoded); }
            catch (UriFormatException)
            {
                throw new ArgumentException("File path contains invalid URL escaping.", nameof(path));
            }

            if (next.Count(c => c == '/') > path.Count(c => c == '/') || next.Contains('\\'))
                throw new ArgumentException("File path must not contain encoded path separators.", nameof(path));
            if (next.Split('/').Any(s => s is "." or ".." || s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("File path must not contain encoded traversal segments.", nameof(path));
            if (next == decoded) break;
            decoded = next;
        }
    }

    public int GetUtf8ByteCount(string value, string parameterName)
    {
        try { return StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Text must contain valid UTF-8 characters.", parameterName);
        }
    }

    private static bool HasScope(ClaimsPrincipal principal, string requiredScope)
    {
        var scopes = principal.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return scopes.Contains(requiredScope, StringComparer.Ordinal);
    }

    private static HashSet<string> ParseSet(string? raw) =>
        (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void ValidateRepoSegment(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || !RepoSegmentPattern.IsMatch(value))
            throw new ArgumentException($"Gitea {label} name is invalid.", label);
    }
}
