# gitea-mcp

[English](README.md) | 简体中文

一个 [MCP](https://modelcontextprotocol.io/) 服务，让 AI 客户端读取 [Gitea](https://about.gitea.com/) 仓库，并在独立 scope、Gitea 账号权限和分支保护约束下写入文件、创建分支与 PR。读取和写入分别使用服务端令牌；客户端只拿 OAuth access token，所以 Gitea 令牌不会交到 Claude、ChatGPT 或其他连上来的客户端手里。

## 三个仓怎么配合

- [nas-auth](https://github.com/ZhengchenTao/nas-auth)：授权服务，负责登录和签发 token。
- [obsidian-mcp](https://github.com/ZhengchenTao/obsidian-mcp)：Obsidian 笔记库的 MCP 服务（可读写）。
- [gitea-mcp](https://github.com/ZhengchenTao/gitea-mcp)：Gitea 的 MCP 服务（读取 + 按账号权限写入）。

MCP 客户端从 MCP 服务的元数据里找到授权服务，自己注册，让用户登录，拿到一个只对这一个 MCP 服务有效的 token。MCP 服务用 nas-auth 公布的公钥验 token，碰不到密码，也不需要和谁共享密钥。三个仓也可以单独用：两个 MCP 服务能接任何标准的 OAuth 服务，nas-auth 也能给任何会验 JWT 的服务做登录。

## 请求怎么进来

```
MCP 客户端（Claude、ChatGPT、Cursor …）
    │ 1. GET /.well-known/oauth-protected-resource   → 该找哪个授权服务
    │ 2. OAuth 授权码 + PKCE                           （对那个授权服务）
    │ 3. POST /mcp，带 Bearer <JWT>（aud=gitea，读需要 read:gitea，写还需要 write:gitea）
    ▼
gitea-mcp
    │ 验 JWT（RS256 走 JWKS，或 HS256 共享密钥）
    │ 读取用只读令牌；写入时改用独立、可选的写令牌
    ▼
Gitea REST API
```

## 工具

全部工具需要 `scope=read:gitea`；写入工具还需要 `scope=write:gitea`。

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
| `list_workflows` | 列出 workflow 的文件名/ID、名称和启用状态 |
| `dispatch_workflow` | 指定 workflow、branch/tag/commit ref 和字符串 inputs 手动触发 CI；workflow 必须支持 `workflow_dispatch` |
| `rerun_workflow_run` | 按 run ID 重跑已有 CI 的全部 jobs |
| `create_branch` | 从分支、tag 或 Commit SHA 创建工作分支 |
| `upsert_file`、`delete_file` | 在工作分支新增 / 更新 / 删除单个 UTF-8 文本文件；每次写入都要给 `expectedHeadSha`，更新 / 删除还要给文件 blob `expectedSha` |
| `commit_files` | 最多 20 个文件的新增、修改和删除；必须给分支 HEAD，修改 / 删除项还要给文件 SHA |
| `create_pull_request` | 从工作分支向目标分支创建 PR |
| `merge_pull_request` | 合并已审查的 PR；必须传当前 PR head SHA，支持 merge / squash / rebase 等方式，拒绝失败或未完成的 CI，不强制合并、不删除源分支 |
| `get_diff` | 读取一个 Commit 的 diff，或比较两个 ref |

`/mcp` 整体仍要求原有 `read:gitea`。写工具另行校验 `write:gitea` 和独立写 PAT；不设写仓白名单，允许操作该 Gitea 账号有写权限的所有仓库。`commit_files` / `upsert_file` / `delete_file` 支持直接提交 `main`、`master` 或默认分支，保留文件 SHA 与分支 HEAD 校验；能否推送由 Gitea 的账号权限与原生分支保护规则决定。`Gitea__WriteProtectedBranches` 可额外禁止指定分支，默认空。文件路径必须是仓库内相对路径，拒绝路径穿越。默认限制为单次最多 20 个文件、单文件 256 KiB、文件内容合计 1 MiB、HTTP 请求体 8 MiB。

合并流程：`read_pull` → 审查 `get_diff` → 将 `head_sha` 传给 `merge_pull_request.expectedHeadSha`。服务先检查当前 head 和已有 CI 状态，再把 `head_commit_id` 交给 Gitea 原子校验；上游要求的审查和检查仍须满足。无 CI 的仓库可以合并，已有 CI 必须成功。超时后先读 PR，不自动重放合并请求。也可直接给 `commit_files.branch=main` 提交，仍需最新 `expectedHeadSha`。

触发 CI：`list_workflows` → `read_file` 检查 workflow 的 `workflow_dispatch.inputs` → `dispatch_workflow(owner, repo, workflowId, reference, inputs)` → `list_workflow_runs` / `read_run_log` 查结果。inputs 的值均为字符串（布尔值传 `"true"` / `"false"`）。返回 `accepted=true` 只表示请求已接收，不表示 CI 已成功；支持时同时返回 `run_id` 与 `html_url`。已有运行可通过 `rerun_workflow_run` 重跑。两个写工具仍要求 `write:gitea` 和 Gitea 账号权限，不设仓库白名单；不自动重试，超时先查运行记录，避免重复构建或部署。

Gitea 重跑可能沿用原 `run_id`，并更新 job ID 与开始/结束时间；用 `read_run_log.jobs` 和时间确认新一次执行，不要把相同 run ID 当成没有触发重跑。

写入错误会以可操作的 MCP tool error 返回，区分 scope / PAT 不足、Gitea 仓库权限或分支规则拒绝、文件 SHA 冲突、分支 HEAD 冲突和超时结果未知。结果未知时先检查分支 / Diff 或 PR，不要盲目重放。

推荐流程：先检查现有文件 → `create_branch` → 在工作分支上用 `list_tree` 获取文件 blob SHA，用 `create_branch` 返回的 SHA 获取初始 HEAD → 用 `read_file` 读取内容 → `commit_files` → 后续提交用返回的 `commit_sha` 作为新 `expectedHeadSha` → `get_diff` → `create_pull_request` → 用 `read_pull` 复核。

当前实例版本为 Gitea 1.27.3。多文件 `POST /repos/{owner}/{repo}/contents` 支持逐文件 SHA 校验，但没有分支 HEAD 前置条件。服务会先把批量 Commit 暂存到唯一临时分支，再通过 `PUT /repos/{owner}/{repo}/branches/{branch}` 携带 `old_commit_id=<expectedHeadSha>`、`force=false` 做 compare-and-swap。HEAD 已变化时目标分支不会被更新。成功或已确认冲突后会清理临时分支；清理失败返回 `cleanup_warning`。请求超时且无法确认目标分支状态时会返回“结果未知”，不会重放批量提交，并保留临时 ref 供人工核查。创建分支 / PR 超时也要先检查现有分支或相同 head/base 的 PR，再决定是否重试。实现依据 [Gitea 1.27 多文件 API](https://docs.gitea.com/api/1.27/operations/repo-change-files/) 和 [条件分支更新 API](https://docs.gitea.com/api/1.27/operations/repo-update-branch/)。

## Gitea 令牌

现有 `Gitea__AdminPat` 只供 API 读取客户端使用。若能单独签发只读 PAT，建议只勾 `read:repository`、`read:organization`、`read:package`、`read:issue`、`read:user`，不加 `write:` 或 `admin:`。工具能看到的就是这个令牌能看到的；用普通用户令牌，本服务就只能看到这个用户的仓库；`Gitea__RepoBlacklist` 可以再额外隐藏仓库。

写入另配一个 `Gitea__WritePat`，使用非管理员账号；该账号只应对目标仓库有写权限（若当前 Gitea 版本支持仓库级 token，就限制到该仓库），PAT 只授予 `write:repository`。Gitea 从 v1.23 起支持细粒度 PAT scope。读 API 客户端仍使用 `Gitea__AdminPat`，所有变更工具只使用独立的 `Gitea__WritePat`；不要给所有 OAuth 客户端共享管理员写 token。`Gitea__WritePat` 留空时写工具关闭；仓库访问范围由该账号的 Gitea 权限决定，不需要配置写仓白名单。

任一令牌泄露：在 Gitea 里撤销并重新签发，只更新对应的 `Gitea__AdminPat` 或 `Gitea__WritePat`，然后重启。启用写入前确认实例版本支持上面两个接口；本地 Mock 测试不会连接 Gitea。

## 配置

用环境变量，`__` 表示层级。

| 变量 | 默认值 | |
|---|---|---|
| `Gitea__BaseUrl` | – | Gitea 根地址，末尾不带斜杠，必填 |
| `Gitea__AdminPat` | – | API 读取客户端凭据；写请求不会使用它，必填 |
| `Gitea__WritePat` | – | 独立的最小权限写令牌，可选；留空即关闭写入 |
| `Gitea__RepoBlacklist` | – | 要隐藏的 `owner/repo`，逗号分隔 |
| `Gitea__WriteProtectedBranches` | 空 | 额外禁止直接提交的分支名；Gitea 自身保护仍然生效 |
| `Gitea__DefaultLimit` | `50` | 列表类工具的默认条数 |
| `Gitea__MaxFileBytes` | `1048576` | 单个文件或日志最多读多少字节 |
| `Gitea__MaxWriteFiles` | `20` | 一次 `commit_files` 最多文件操作数 |
| `Gitea__MaxWriteFileBytes` | `262144` | 单个写入文件最大 UTF-8 字节数 |
| `Gitea__MaxWriteRequestBytes` | `1048576` | 一次写入的文件内容总字节上限 |
| `Gitea__MaxMcpRequestBodyBytes` | `8388608` | MCP HTTP 请求体上限 |
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

再在 nas-auth 的 `resources.json` 里给 `gitea` 条目增加 `write:gitea`，保留 `read:gitea`。先确认一次性迁移标记 `user_resources_seeded_v1=1`；若尚未完成，首次 seed 会给符合 `admin_only` 的现有用户授予资源中列出的全部 scope，不能直接把新 scope 带入首次 seed。seed 完成后，配置变化不会自动扩大现有 `user_resources` 授权，管理员必须只给指定用户显式授予 `read:gitea write:gitea`。资源的 `admin_only=true` 会继续拒绝非管理员，即使该用户有授权行；只有指定用户是普通用户时才需要把它改为 `false`。修改资源配置后重启 nas-auth，再让 MCP 客户端重新授权。Token 需要同时包含两个 scope，因为 `/mcp` 保留原有 `read:gitea` 路由门禁。

## 用哪个授权服务

Claude 这类远程连接器客户端只走完整的 OAuth 流程：从本服务的元数据发现授权服务，授权码 + PKCE，没有「直接贴一个 token」的选项。所以授权服务得支持 PKCE、动态客户端注册（客户端要自己注册）、`resource` 参数（RFC 8707）和自定义 scope（读取用 `read:gitea`，写入另需 `write:gitea`）。

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

真实 MCP/Gitea 集成测试脚本见
[`Run-GiteaWriteIntegration.py`](gitea-mcp.Tests/Run-GiteaWriteIntegration.py)。
它只允许精确的测试仓（`zhengchen.tao/gitea-mcp-write-test`，或现有的
`zhengchen.tao/gitea-pr-selftest` 沙箱）。读取使用本地 MCP 读取客户端的
`Gitea__AdminPat`，写入使用独立非管理员账号的 `Gitea__WritePat`。该 bot 账号只能写测试仓，
PAT 只授予 `write:repository`；`AdminPat` 只供读取 API，所有变更都由 `WritePat` 发出。
默认分支和另一个非默认分支都必须受保护，测试账号不能绕过。写入前脚本会显示目标仓库、分支和文件计划，
并要求输入绑定到仓库与分支的确认文本。脚本保留测试分支和 PR 供复核，不删除仓库或测试数据。
调用前设置 `GITEA_TEST_INSTANCE`、`GITEA_TEST_OWNER`、`GITEA_TEST_REPO`、
`GITEA_TEST_ACCOUNT`、`GITEA_TEST_READ_PAT`（读取客户端 AdminPat）、
`GITEA_TEST_WRITE_PAT` 和 `GITEA_TEST_JWT_KEY`；脚本启动本地 MCP 服务，其自身调用只针对指定测试仓，
不改 nas-auth 或部署配置，也不会打印凭据。测试仓必须是私有且未归档。构建后运行
`python .\gitea-mcp.Tests\Run-GiteaWriteIntegration.py`。

## CI

`.gitea/workflows/build-image.yml` 在每次推送到 `main` 时构建并推送 `<REGISTRY>/<IMAGE_OWNER>/gitea-mcp`，然后可以经 SSH 触发重新部署。用到 `vars.REGISTRY`、`vars.IMAGE_OWNER`、`secrets.AIFACELY_REGISTRY_TOKEN`，部署步骤另外用 `vars.DEPLOY_SERVICE`、`secrets.NAS_CI_SSH_KEY`、`secrets.NAS_SSH_HOST`、`secrets.NAS_SSH_KNOWN_HOSTS`。里面的 action 地址和构建代理是按我自己的 CI 写的，fork 后改成你的。

## 许可

MIT
