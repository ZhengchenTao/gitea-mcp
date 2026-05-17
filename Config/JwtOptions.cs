namespace GiteaMcp.Config;

/// <summary>
/// JWT 验签配置，与 auth server 共用同款 HS256 对称密钥。
/// 环境变量：Jwt__Issuer, Jwt__Audience, Jwt__SigningKey__Current, Jwt__SigningKey__Previous
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>期望的 iss claim（你的 auth server 的 issuer URL），必须通过 env 注入</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>期望的 aud claim，默认 gitea</summary>
    public string Audience { get; set; } = "gitea";

    public SigningKeyPair SigningKey { get; set; } = new();

    public class SigningKeyPair
    {
        /// <summary>当前签名密钥（HS256 对称密钥），env: Jwt__SigningKey__Current</summary>
        public string Current { get; set; } = string.Empty;

        /// <summary>上一轮密钥，密钥轮换过渡期用，env: Jwt__SigningKey__Previous（可为空）</summary>
        public string? Previous { get; set; }
    }
}
