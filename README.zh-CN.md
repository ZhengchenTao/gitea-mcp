# gitea-mcp

[English](README.md) | 简体中文

一个 [MCP](https://modelcontextprotocol.io/) 服务，让 AI 客户端只读地访问 [Gitea](https://about.gitea.com/)：仓库、文件、提交、issue、PR、软件包和 Actions 运行记录。它用一个只读的个人访问令牌去调 Gitea，这个令牌只留在服务端；客户端用 OAuth access token 访问本服务，所以 Gitea 令牌不会交到 Claude、ChatGPT 或其他连上来的客户端手里。

## 三个仓怎么配合

- [nas-auth](https://github.com/ZhengchenTao/nas-auth)：授权服务，负责登录和签发 token。
- [obsidian-mcp](https://github.com/ZhengchenTao/obsidian-mcp)：Obsidian 笔记库的 MCP 服务（可读写）。
- [gitea-mcp](https://github.com/ZhengchenTao/gitea-mcp)：Gitea 的 MCP 服务（只读）。

MCP 客户端从 MCP 服务的元数据里找到授权服务，自己注册，让用户登录，拿到一个只对这一个 MCP 服务有效的 token。MCP 服务用 nas-auth 公布的公钥验 token，碰不到密码，也不需要和谁共享密钥。三个仓也可以单独用：两个 MCP 服务能接任何标准的 OAuth 服务，nas-auth 也能给任何会验 JWT 的服务做登录。

## 请求怎么进来

```
MCP 客户端（Claude、ChatGPT、Cursor …）
    │ 1. GET /.well-known/oauth-protected-resource   → 该找哪个授权服务
    │ 2. OAuth 授权码 + PKCE                           （对那个授权服务）
    │ 3. POST /mcp，带 Bearer <JWT>（aud=gitea，scope 为 read:gitea）
    ▼
gitea-mcp
    │ 验 JWT（RS256 走 JWKS，或 HS256 共享密钥）
    │ 用自己的只读令牌调 Gitea
    ▼
Gitea REST API
```

## 工具

全部需要 `scope=read:gitea`。

| 工具 | |
|---|---|
| `list_repos`、`read_repo` | 令牌能看到的仓库（个人和组织、公开和私有）；topic、默认分支、是否镜像 |
| `list_tree`、`read_file` | 某个 ref 下的文件树（最多 500 条）；文件原文，超过 `MaxFileBytes` 截断 |
| `search_code` | 代码搜索，需要 Gitea 开了索引 |
| `list_branches`、`list_commits`、`read_commit` | 分支及各自最新提交；最近的提交；单个提交和逐文件 diff（最多 50 个文件） |
| `list_issues`、`read_issue` | 按状态列 issue；单个 issue 及评论 |
| `list_pulls`、`read_pull` | 按状态列 PR；单个 PR 及审查评论、改动文件 |
| `list_orgs`、`read_org` | 组织 |
| `list_packages`、`read_package` | 按 owner 列软件包（容器、generic、npm …）；单个版本 |
| `list_workflow_runs`、`read_run_log` | Actions 运行记录；单次运行的 job 和日志末尾 |

## Gitea 令牌

在 Gitea → 设置 → 应用 里创建，只勾这几个 scope：`read:repository`、`read:organization`、`read:package`、`read:issue`、`read:user`。不要勾任何 `write:` 或 `admin:`。工具能看到的就是这个令牌能看到的，用普通用户的令牌，本服务就只能看到这个用户的仓库；`Gitea__RepoBlacklist` 可以再额外隐藏几个。

令牌泄露了：在 Gitea 里撤销，重新建一个，更新 `Gitea__AdminPat`，重启。

## 配置

用环境变量，`__` 表示层级。

| 变量 | 默认值 | |
|---|---|---|
| `Gitea__BaseUrl` | – | Gitea 根地址，末尾不带斜杠，必填 |
| `Gitea__AdminPat` | – | 上面那个只读令牌，必填 |
| `Gitea__RepoBlacklist` | – | 要隐藏的 `owner/repo`，逗号分隔 |
| `Gitea__DefaultLimit` | `50` | 列表类工具的默认条数 |
| `Gitea__MaxFileBytes` | `1048576` | 单个文件或日志最多读多少字节 |
| `Jwt__Algorithm` | `HS256` | `RS256`（从 issuer 的 JWKS 取公钥）或 `HS256`（共享密钥） |
| `Jwt__Issuer` | – | 期望的 `iss`，必填。RS256 模式从 `<Issuer>/.well-known/openid-configuration` 拉公钥。 |
| `Jwt__Audience` | `gitea` | 期望的 `aud` |
| `Jwt__ValidTypes__N` | – | 仅 RS256：允许的 `typ` 头。对接 nas-auth 设 `at+jwt`，因为它的 id_token 用的是同一把钥。留空不检查，兼容 token 里写 `typ: JWT` 的服务。 |
| `Jwt__SigningKey__Current` / `__Previous` | – | 仅 HS256：和授权服务共享的密钥，以及轮换期间的上一把 |
| `Mcp__OAuthDiscovery__Issuer` | – | 必填，写进本服务的 OAuth 元数据 |
| `Mcp__OAuthDiscovery__AuthorizationEndpoint` | – | 必填 |
| `Mcp__OAuthDiscovery__TokenEndpoint` | – | 必填 |
| `Mcp__OAuthDiscovery__RegistrationEndpoint` | – | 授权服务的 `/register`，支持动态注册时填 |
| `Mcp__OAuthDiscovery__ResourceUrl` | 请求的 host | RFC 9728 的 `resource`，要和授权服务那边一致 |

对接 nas-auth 就是这些：

```
Jwt__Algorithm=RS256
Jwt__Issuer=https://auth.example.com
Jwt__ValidTypes__0=at+jwt
Mcp__OAuthDiscovery__Issuer=https://auth.example.com
Mcp__OAuthDiscovery__AuthorizationEndpoint=https://auth.example.com/authorize
Mcp__OAuthDiscovery__TokenEndpoint=https://auth.example.com/token
Mcp__OAuthDiscovery__RegistrationEndpoint=https://auth.example.com/register
Mcp__OAuthDiscovery__ResourceUrl=https://gitea-mcp.example.com
```

再在 nas-auth 的 `resources.json` 里加一个 `gitea` 条目，`resource_url` 填同一个地址，scope 为 `read:gitea`。

## 用哪个授权服务

Claude 这类远程连接器客户端只走完整的 OAuth 流程：从本服务的元数据发现授权服务，授权码 + PKCE，没有「直接贴一个 token」的选项。所以授权服务得支持 PKCE、动态客户端注册（客户端要自己注册）、`resource` 参数（RFC 8707）和自定义 scope（`read:gitea`）。

- [nas-auth](https://github.com/ZhengchenTao/nas-auth)：本服务就是照着它写的。小，自建，按上面的 RS256 配置即可。
- 托管服务：[Logto](https://logto.io)、[ZITADEL](https://zitadel.com)、[Auth0](https://auth0.com)。用 RS256 模式，填租户的 issuer 地址。
- 自建、功能更全：[Keycloak](https://www.keycloak.org)、[Authentik](https://goauthentik.io)，或者自己部署 ZITADEL / Logto。
- HS256 模式留给你自己写的极简授权服务，两边要用同一把密钥。

## 运行

```bash
docker build -t gitea-mcp .

docker run --rm -p 8080:8080 \
  -e Gitea__BaseUrl=https://gitea.example.com \
  -e Gitea__AdminPat=$GITEA_PAT \
  -e Jwt__Algorithm=RS256 \
  -e Jwt__Issuer=https://auth.example.com \
  -e Jwt__ValidTypes__0=at+jwt \
  -e Mcp__OAuthDiscovery__Issuer=https://auth.example.com \
  -e Mcp__OAuthDiscovery__AuthorizationEndpoint=https://auth.example.com/authorize \
  -e Mcp__OAuthDiscovery__TokenEndpoint=https://auth.example.com/token \
  gitea-mcp
```

本地开发用 HS256、自己签一个 token 最省事：

```bash
export Gitea__BaseUrl=https://gitea.example.com
export Gitea__AdminPat=<只读令牌>
export Jwt__Issuer=https://auth.example.com
export Jwt__SigningKey__Current=dev-secret-key-at-least-32-chars-long
export Mcp__OAuthDiscovery__Issuer=https://auth.example.com
export Mcp__OAuthDiscovery__AuthorizationEndpoint=https://auth.example.com/authorize
export Mcp__OAuthDiscovery__TokenEndpoint=https://auth.example.com/token
dotnet run

dotnet user-jwts create --issuer https://auth.example.com --audience gitea \
  --name tester --claim sub=tester --claim scope=read:gitea

npx @modelcontextprotocol/inspector
# 选 Streamable HTTP，地址 http://localhost:5000/mcp，把 token 填进 Bearer
```

测试：`dotnet test gitea-mcp.Tests`。

## CI

`.gitea/workflows/build-image.yml` 在每次推送到 `main` 时构建并推送 `<REGISTRY>/<IMAGE_OWNER>/gitea-mcp`，然后可以经 SSH 触发重新部署。用到 `vars.REGISTRY`、`vars.IMAGE_OWNER`、`secrets.AIFACELY_REGISTRY_TOKEN`，部署步骤另外用 `vars.DEPLOY_SERVICE`、`secrets.NAS_CI_SSH_KEY`、`secrets.NAS_SSH_HOST`、`secrets.NAS_SSH_KNOWN_HOSTS`。里面的 action 地址和构建代理是按我自己的 CI 写的，fork 后改成你的。

## 许可

MIT
