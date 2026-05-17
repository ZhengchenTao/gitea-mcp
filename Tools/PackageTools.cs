using GiteaMcp.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace GiteaMcp.Tools;

/// <summary>Package Registry Tool：list_packages / read_package</summary>
[McpServerToolType]
public class PackageTools(GiteaApiClient gitea)
{
    [McpServerTool]
    [Description(
        "List packages in Gitea's built-in package registry for a given owner (user or org). " +
        "Supports all package types: container (Docker/OCI), generic, npm, pypi, maven, nuget, etc. " +
        "Returns: package name, type, version, creator, and creation date. " +
        "Use read_package to get detailed metadata (e.g. OCI manifest digest for container images).")]
    public async Task<object> list_packages(
        [Description("Owner (user login or org name) whose packages to list.")] string owner,
        [Description("Package type filter: 'container', 'generic', 'npm', 'pypi', 'maven', 'nuget', etc. Omit for all types.")] string? type = null,
        [Description("Max packages to return. Default 50.")] int? limit = null,
        CancellationToken ct = default)
    {
        var lim = Math.Min(limit ?? 50, 50);
        var packages = await gitea.GetPackagesAsync(owner, type, lim, ct);

        return packages.Select(p => new
        {
            owner = p.Owner?.Login,
            name = p.Name,
            type = p.Type,
            version = p.Version,
            creator = p.Creator?.Login,
            created = p.Created,
        }).ToList();
    }

    [McpServerTool]
    [Description(
        "Get metadata for a specific package version in Gitea's package registry. " +
        "For container images, this includes the OCI manifest digest and image details. " +
        "Use list_packages to discover available packages and their versions.")]
    public async Task<object> read_package(
        [Description("Owner (user login or org name).")] string owner,
        [Description("Package type: 'container', 'generic', 'npm', etc.")] string type,
        [Description("Package name.")] string name,
        [Description("Package version string (e.g. 'latest', '1.0.0', 'main').")] string version,
        CancellationToken ct = default)
    {
        var pkg = await gitea.GetPackageAsync(owner, type, name, version, ct);

        return new
        {
            owner = pkg.Owner?.Login,
            name = pkg.Name,
            type = pkg.Type,
            version = pkg.Version,
            creator = pkg.Creator?.Login,
            created = pkg.Created,
        };
    }
}
