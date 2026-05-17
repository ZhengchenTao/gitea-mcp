namespace GiteaMcp.Config;

/// <summary>
/// JWT 验签配置。
/// 环境变量：Jwt__Algorithm, Jwt__Issuer, Jwt__Audience, Jwt__SigningKey__Current, Jwt__SigningKey__Previous
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// JWT 签名算法。
    /// <list type="bullet">
    ///   <item><c>HS256</c>（默认）：与 AS 共享对称密钥（SigningKey.Current 必填）。适合自建极简 AS。</item>
    ///   <item><c>RS256</c>：从 Issuer 走 OIDC discovery 自动拉 JWKS（含自动刷新）。
    ///     适合任何标准 OAuth 2.1 / OIDC AS（Logto / ZITADEL / Keycloak / Auth0 等）。
    ///     要求 Issuer 暴露 <c>/.well-known/openid-configuration</c>。</item>
    /// </list>
    /// </summary>
    public string Algorithm { get; set; } = "HS256";

    /// <summary>期望的 iss claim（你的 auth server 的 issuer URL），必须通过 env 注入</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>期望的 aud claim，默认 gitea</summary>
    public string Audience { get; set; } = "gitea";

    /// <summary>HS256 模式使用；RS256 模式下忽略。</summary>
    public SigningKeyPair SigningKey { get; set; } = new();

    public class SigningKeyPair
    {
        /// <summary>当前签名密钥（HS256 对称密钥），env: Jwt__SigningKey__Current</summary>
        public string Current { get; set; } = string.Empty;

        /// <summary>上一轮密钥，密钥轮换过渡期用，env: Jwt__SigningKey__Previous（可为空）</summary>
        public string? Previous { get; set; }
    }
}
