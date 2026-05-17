using GiteaMcp.Config;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace GiteaMcp.Auth;

/// <summary>
/// JWT Bearer 验签配置。根据 JwtOptions.Algorithm 选择：
/// <list type="bullet">
///   <item><c>HS256</c>：与 AS 共享对称密钥（Current + Previous 双密钥用于轮换）。</item>
///   <item><c>RS256</c>：委托 ASP.NET Core 通过 OIDC discovery 拉 JWKS，
///     自动刷新公钥，AS 端密钥轮换无需重启本服务。</item>
/// </list>
/// </summary>
public static class JwtBearerSetup
{
    public static IServiceCollection AddGiteaJwtBearer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwtOpts = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? new JwtOptions();

        var algorithm = (jwtOpts.Algorithm ?? "HS256").Trim().ToUpperInvariant();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // 关闭默认的入站 claim type 映射，否则 "sub"/"scope" 会被改写成
                // ClaimTypes.NameIdentifier 之类的长 URI，下游 FindFirst("scope") 取不到。
                options.MapInboundClaims = false;

                var tvp = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOpts.Issuer,

                    ValidateAudience = true,
                    ValidAudience = jwtOpts.Audience,

                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    // 允许 2 分钟时钟偏差，缓解 AS 与本服务时钟漂移
                    ClockSkew = TimeSpan.FromMinutes(2),
                };

                if (algorithm == "RS256")
                {
                    if (string.IsNullOrWhiteSpace(jwtOpts.Issuer))
                        throw new InvalidOperationException(
                            "Jwt:Issuer 未配置，RS256 模式无法启动。");

                    // Authority = Issuer 时，JwtBearer 会自动从
                    // <Issuer>/.well-known/openid-configuration 拉 OIDC 元数据，
                    // 再从其中的 jwks_uri 拉公钥并周期性刷新。IssuerSigningKeys
                    // 由 ConfigurationManager 在验签时动态注入，无需在此设置。
                    options.Authority = jwtOpts.Issuer;
                    options.RequireHttpsMetadata = !IsLocalhostUrl(jwtOpts.Issuer);
                }
                else if (algorithm == "HS256")
                {
                    // ToList 物化一次，避免每次验签重建 SymmetricSecurityKey。
                    tvp.IssuerSigningKeys = BuildHs256Keys(jwtOpts).ToList();
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Jwt:Algorithm '{jwtOpts.Algorithm}' 不支持。可选值：HS256, RS256");
                }

                options.TokenValidationParameters = tvp;
            });

        return services;
    }

    /// <summary>
    /// 构建 Current + Previous 双 HS256 密钥列表，供 SDK 依次尝试验签。
    /// 轮换期间两把钥匙同时有效，过期的旧 Token 会被 ValidateLifetime 自然拦截。
    /// Current 未配置直接抛错，避免容器静默以"任何 token 都不通过"的状态运行。
    /// </summary>
    private static IEnumerable<SecurityKey> BuildHs256Keys(JwtOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.SigningKey.Current))
            throw new InvalidOperationException(
                "Jwt:SigningKey:Current 未配置，HS256 模式无法启动。" +
                "请通过 env Jwt__SigningKey__Current 注入与 auth server 共享的 HS256 密钥。");

        yield return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opts.SigningKey.Current));

        if (!string.IsNullOrWhiteSpace(opts.SigningKey.Previous))
            yield return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opts.SigningKey.Previous));
    }

    private static bool IsLocalhostUrl(string url) =>
        url.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);
}
