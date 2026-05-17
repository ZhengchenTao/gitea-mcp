using Microsoft.AspNetCore.Authorization;

namespace GiteaMcp.Auth;

/// <summary>
/// 自定义 scope 授权策略：要求 JWT 的 scope claim 包含指定值。
/// 对应 RequireScope("read:gitea") policy。
/// </summary>
public static class ScopePolicies
{
    public const string ReadGitea = "read:gitea";

    public static IServiceCollection AddScopePolicies(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(ReadGitea, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeRequirement(ReadGitea));
            });

        services.AddSingleton<IAuthorizationHandler, ScopeRequirementHandler>();
        return services;
    }
}

/// <summary>
/// 授权要求：JWT scope claim 必须包含指定的 scope 字符串（空格分隔的多 scope 支持）。
/// </summary>
public class ScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}

/// <summary>
/// 验证 scope claim。
/// 标准实现里 JWT 的 scope 是单字符串（空格分隔），形如 "read:gitea" 或 "read:gitea write:gitea"。
/// 兼容部分实现把多 scope 拆成多个 claim 的形式（FindAll）。
/// OAuth 2.0 (RFC 6749 §3.3) 规定 scope 大小写敏感。
/// </summary>
public class ScopeRequirementHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        var scopes = context.User
            .FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);

        if (scopes.Contains(requirement.Scope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
