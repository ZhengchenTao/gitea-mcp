# gitea-mcp

[English](README.md) | 简体中文

通过 [MCP（Model Context Protocol）](https://modelcontextprotocol.io/) 协议以只读方式访问 [Gitea](https://about.gitea.com/) 实例，访问由 OAuth 签发的 JWT bearer token 控制。

MCP server 内部持有一个只读的 Gitea Personal Access Token（PAT），绝不暴露给 MCP client。Client 改为用 OAuth 签发的 JWT 向本 server 认证。

支持两种 JWT 签名模式：

- **HS256**（默认）—— AS 与本 server 共享对称密钥。用于自建的极简 AS。
- **RS256** —— 自动通过 `<Issuer>/.well-known/openid-configuration` 的 OIDC discovery 拉取 JWKS。可对接任意标准 provider：[Logto](https://logto.io)、[ZITADEL](https://zitadel.com)、[Keycloak](https://www.keycloak.org)、[Auth0](https://auth0.com) 等。

部署指引见下方 [Choosing an AS](#choosing-an-as)。

## Architecture

```
MCP client (Claude.ai, etc.)
    │
    │ ① GET /.well-known/oauth-authorization-server  (RFC 8414)
    │ ② OAuth Authorization Code + PKCE  (against your AS)
    │ ③ Bearer JWT (aud=gitea, scope=read:gitea)
    │
    ▼
gitea-mcp /mcp
    │  JWT verify (HS256, shared key with AS)
    │  Bearer <GITEA_ADMIN_PAT>  (held server-side, never leaves the process)
    │
    ▼
Gitea REST API
```

## Tools

| 工具 | 说明 |
|------|-------------|
| `list_repos` | 列出 PAT 可见的所有仓库（个人 + 组织，公开 + 私有） |
| `read_repo` | 仓库元数据：topics、stars、默认分支、mirror 标记 |
| `list_tree` | 指定 ref 下的文件树（可递归，最多 500 条） |
| `read_file` | 文件原始内容（UTF-8，按 `MaxFileBytes` 截断） |
| `search_code` | 通过 Gitea indexer 做代码搜索（需实例开启 indexer） |
| `list_branches` | 分支列表 + 每个分支的最新 commit |
| `list_commits` | 最近的 commit（含 author + message） |
| `read_commit` | 完整 commit 详情 + per-file diff（最多 50 个文件） |
| `list_issues` | 按 state（open/closed/all）过滤的 issue 列表 |
| `read_issue` | issue 正文 + 所有评论 |
| `list_pulls` | 按 state 过滤的 PR 列表 |
| `read_pull` | PR 正文 + review comments + 变更文件 |
| `list_orgs` | PAT 可见的所有 organization |
| `read_org` | organization 元数据 |
| `list_packages` | 按 owner 列出 registry 内的 packages（container/generic/npm/...） |
| `read_package` | package 版本元数据 |
| `list_workflow_runs` | Gitea Actions workflow run 历史 |
| `read_run_log` | run 详情 + job 列表 + 日志（按 `MaxFileBytes` 截断） |

所有工具调用都需要带有 `scope=read:gitea` 的有效 JWT。

## Configuration

| 变量 | 默认值 | 必填 | 说明 |
|----------|---------|---|-------------|
| `Gitea__BaseUrl` | — | **是** | Gitea 根 URL，结尾不带斜杠 |
| `Gitea__AdminPat` | — | **是** | Gitea 只读 PAT —— 见下方 PAT setup |
| `Gitea__RepoBlacklist` | *(空)* | 否 | 逗号分隔的 `owner/repo` 列表，用于屏蔽仓库 |
| `Gitea__DefaultLimit` | `50` | 否 | list 类操作的默认分页大小 |
| `Gitea__MaxFileBytes` | `1048576` | 否 | 文件 / 日志读取上限（1 MB） |
| `Jwt__Algorithm` | `HS256` | 否 | `HS256` 或 `RS256` |
| `Jwt__Issuer` | — | **是** | 期望的 `iss` claim —— 你 AS 的 issuer URL |
| `Jwt__Audience` | `gitea` | 否 | 期望的 `aud` claim |
| `Jwt__SigningKey__Current` | — | 仅 HS256 | HS256 签名密钥，与你的 AS 共享 |
| `Jwt__SigningKey__Previous` | — | 否 | 轮换窗口内的旧 HS256 密钥 |
| `Mcp__OAuthDiscovery__Issuer` | — | **是** | `/.well-known` 中的 `issuer` 字段 |
| `Mcp__OAuthDiscovery__AuthorizationEndpoint` | — | **是** | 你 AS 的 `/authorize` URL |
| `Mcp__OAuthDiscovery__TokenEndpoint` | — | **是** | 你 AS 的 `/token` URL |
| `Mcp__OAuthDiscovery__RegistrationEndpoint` | — | 否 | 你 AS 的 `/register` URL（DCR） |
| `Mcp__OAuthDiscovery__ResourceUrl` | request host | 否 | RFC 9728 中的 `resource` 标识 |
| `ASPNETCORE_ENVIRONMENT` | `Production` | 否 | `Development` 启用详细日志 |

配置 key 用 ASP.NET Core 的 double-underscore 嵌套约定（`Jwt__SigningKey__Current` ⇔ `Jwt:SigningKey:Current`）。

## PAT setup (Gitea → Settings → Applications)

按"最小权限原则"生成 token，**仅勾选**以下 scope：

- `read:repository`
- `read:organization`
- `read:package`
- `read:issue`
- `read:user`

**不要**授予 `write:*` 或 `admin:*` 任何 scope。

PAT 一旦泄露：在 Gitea 撤销 → 生成新的 → 更新 `Gitea__AdminPat` 环境变量 → 重启容器。

## Local development

```bash
# 1. Restore 与配置
dotnet restore
export Gitea__BaseUrl=https://gitea.example.com
export Gitea__AdminPat=<你的只读 PAT>
export Jwt__Issuer=https://your-auth-server.example.com
export Jwt__SigningKey__Current=dev-secret-key-at-least-32-chars-long
export Mcp__OAuthDiscovery__Issuer=https://your-auth-server.example.com
export Mcp__OAuthDiscovery__AuthorizationEndpoint=https://your-auth-server.example.com/authorize
export Mcp__OAuthDiscovery__TokenEndpoint=https://your-auth-server.example.com/token

dotnet run
# 监听 http://localhost:5000

# 2. 生成开发用 JWT
dotnet user-jwts create \
  --issuer https://your-auth-server.example.com \
  --audience gitea \
  --name tester \
  --claim sub=tester \
  --claim scope=read:gitea

# 3. 用 MCP Inspector 测试
npx @modelcontextprotocol/inspector
# Transport: Streamable HTTP
# URL: http://localhost:5000/mcp
# Bearer Token: <步骤 2 得到的 token>

# 4. 跑单元测试
dotnet test gitea-mcp.Tests/
```

## Docker

```bash
docker build -t gitea-mcp .

docker run --rm -p 8080:8080 \
  -e Gitea__BaseUrl=https://gitea.example.com \
  -e Gitea__AdminPat=$GITEA_PAT \
  -e Jwt__Issuer=https://your-auth-server.example.com \
  -e Jwt__SigningKey__Current=$JWT_SIGNING_KEY \
  -e Mcp__OAuthDiscovery__Issuer=https://your-auth-server.example.com \
  -e Mcp__OAuthDiscovery__AuthorizationEndpoint=https://your-auth-server.example.com/authorize \
  -e Mcp__OAuthDiscovery__TokenEndpoint=https://your-auth-server.example.com/token \
  gitea-mcp
```

仓库内的 `.gitea/workflows/build-image.yml` 在每次推送到 `main` 时构建并推送镜像。需要在仓库设置中配置：

- `vars.REGISTRY` —— registry 主机名（例如 `ghcr.io`）
- `vars.IMAGE_OWNER` —— registry 的 owner / namespace
- `secrets.PACKAGES_TOKEN` —— registry 推送 token

## Choosing an AS

Claude.ai 网页端强制走完整的 OAuth Authorization Code + PKCE 流程，对接你 MCP server 的 `/.well-known/oauth-authorization-server` 端点 —— 没有 bearer token 捷径可用。在下面几条路径中选一条：

**Hosted (fastest start, recommended for new setups)** —— RS256 模式：

| Provider | 免费额度 | 备注 |
|---|---|---|
| [Logto Cloud](https://logto.io) | 5000 MAU | 最轻量，约 30 分钟搭好 |
| [ZITADEL Cloud](https://zitadel.com) | 25k auths / 月 | 功能更全，文档稍重 |

设置 `Jwt__Algorithm=RS256` 与 `Jwt__Issuer=<你的 tenant issuer URL>`。公钥会自动从 `<Issuer>/.well-known/openid-configuration` 拉取。

**Self-hosted, full-featured** —— RS256 模式：
[Keycloak](https://www.keycloak.org)、[ZITADEL](https://github.com/zitadel/zitadel)、[Logto](https://github.com/logto-io/logto)、[Authentik](https://goauthentik.io)。

**Self-hosted, minimal** —— HS256 模式：
参见 [nas-auth](https://github.com/ZhengchenTao/nas-auth) —— 本 server 在开发过程中对接的那个约 500 行 LoC 的参考 AS。或者自己写一个。MCP server 的 `Jwt__SigningKey__Current` 必须与 AS 的签名密钥保持一致。

**不论选哪条，AS 必须支持：**
- OAuth 2.1 + PKCE（RFC 7636）
- Dynamic Client Registration（RFC 7591）—— 让 Claude.ai 能自助注册
- `resource` 参数（RFC 8707）—— 用于签发 audience-bound token
- 自定义 scope 支持（`read:gitea`）

## License

MIT
