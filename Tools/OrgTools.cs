using GiteaMcp.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace GiteaMcp.Tools;

/// <summary>Organization Tool：list_orgs / read_org</summary>
[McpServerToolType]
public class OrgTools(GiteaApiClient gitea)
{
    [McpServerTool]
    [Description(
        "List all Gitea organizations visible to the admin token. " +
        "Returns: org name, full name, description, visibility, and website. " +
        "Use read_org to get repo/member counts for a specific org. " +
        "Tip: after listing orgs, call list_repos with owner=<org_name> to see that org's repos.")]
    public async Task<object> list_orgs(
        [Description("Max orgs to return. Default 50.")] int? limit = null,
        CancellationToken ct = default)
    {
        var lim = Math.Min(limit ?? 50, 50);
        var orgs = await gitea.GetOrgsAsync(lim, ct);

        return orgs.Select(o => new
        {
            name = o.Name,
            full_name = o.FullName,
            description = o.Description,
            website = o.Website,
            location = o.Location,
            visibility = o.Visibility,
        }).ToList();
    }

    [McpServerTool]
    [Description(
        "Get detailed information about a specific Gitea organization: " +
        "description, website, location, and visibility setting. " +
        "Use list_repos with owner=<name> to see the org's repositories.")]
    public async Task<object> read_org(
        [Description("Organization name (login).")] string name,
        CancellationToken ct = default)
    {
        var org = await gitea.GetOrgAsync(name, ct);

        return new
        {
            name = org.Name,
            full_name = org.FullName,
            description = org.Description,
            website = org.Website,
            location = org.Location,
            visibility = org.Visibility,
        };
    }
}
