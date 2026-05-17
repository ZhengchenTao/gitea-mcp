using GiteaMcp.Config;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace GiteaMcp.Auth;

/// <summary>
/// JWT Bearer 验签配置，HS256 对称密钥方案。
/// ValidIssuer / ValidAudience 由配置驱动；支持 Current + Previous 双密钥（轮换窗口）。
/// </summary>
public static class JwtBearerSetup
{
    public static IServiceCollection AddGiteaJwtBearer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwtOpts = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? new JwtOptions();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // 关闭默认的入站 claim type 映射，否则 "sub"/"scope" 会被改写成
                // ClaimTypes.NameIdentifier 之类的长 URI，下游 FindFirst("scope") 取不到。
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOpts.Issuer,

                    ValidateAudience = true,
                    ValidAudience = jwtOpts.Audience,

                    ValidateIssuerSigningKey = true,
                    // ToList 物化一次，避免每次验签重建 SymmetricSecurityKey。
                    IssuerSigningKeys = BuildSigningKeys(jwtOpts).ToList(),

                    ValidateLifetime = true,
                    // 允许 2 分钟时钟偏差，缓解 AS 与本服务时钟漂移
                    ClockSkew = TimeSpan.FromMinutes(2),
                };
            });

        return services;
    }

    /// <summary>
    /// 构建 Current + Previous 双密钥列表，供 SDK 依次尝试验签。
    /// 轮换期间两把钥匙同时有效，过期的旧 Token 会被 ValidateLifetime 自然拦截。
    /// Current 未配置直接抛错，避免容器静默以"任何 token 都不通过"的状态运行。
    /// </summary>
    private static IEnumerable<SecurityKey> BuildSigningKeys(JwtOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.SigningKey.Current))
            throw new InvalidOperationException(
                "Jwt:SigningKey:Current 未配置，gitea-mcp 无法启动。" +
                "请通过 env Jwt__SigningKey__Current 注入与 auth server 共享的 HS256 密钥。");

        yield return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opts.SigningKey.Current));

        if (!string.IsNullOrWhiteSpace(opts.SigningKey.Previous))
            yield return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opts.SigningKey.Previous));
    }
}
