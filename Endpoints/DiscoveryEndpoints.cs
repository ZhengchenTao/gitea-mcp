using GiteaMcp.Config;
using Microsoft.Extensions.Options;

namespace GiteaMcp.Endpoints;

/// <summary>
/// 两个 well-known 端点：
/// 1. /.well-known/oauth-authorization-server (RFC 8414)：指向配置的 AS。
/// 2. /.well-known/oauth-protected-resource (RFC 9728)：声明本资源的 identifier
///    + 指向哪个 AS。客户端读到 PRM 后会在 /authorize 请求里带
///    resource=<identifier>，满足 AS 的 RFC 8707 校验。
/// </summary>
public static class DiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapDiscovery(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-authorization-server", (IOptions<McpDiscoveryOptions> opts) =>
        {
            var o = opts.Value;
            return Results.Ok(new
            {
                issuer = o.Issuer,
                authorization_endpoint = o.AuthorizationEndpoint,
                token_endpoint = o.TokenEndpoint,
                registration_endpoint = o.RegistrationEndpoint,
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                code_challenge_methods_supported = new[] { "S256" },
                scopes_supported = new[] { "read:gitea" },
            });
        });

        app.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx, IOptions<McpDiscoveryOptions> opts) =>
        {
            var o = opts.Value;
            var resourceUrl = string.IsNullOrWhiteSpace(o.ResourceUrl)
                ? $"{ctx.Request.Scheme}://{ctx.Request.Host}"
                : o.ResourceUrl;
            return Results.Ok(new
            {
                resource = resourceUrl,
                authorization_servers = new[] { o.Issuer },
                scopes_supported = new[] { "read:gitea" },
                bearer_methods_supported = new[] { "header" },
            });
        });

        return app;
    }
}
