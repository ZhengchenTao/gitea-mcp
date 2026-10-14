# gitea-mcp

English | [简体中文](README.zh-CN.md)

An [MCP](https://modelcontextprotocol.io/) server that gives an AI client
read access and tightly scoped write access to a [Gitea](https://about.gitea.com/) instance: repositories,
files, commits, issues, pull requests, packages and Actions runs. It talks to
Gitea with a read-only personal access token and, when enabled, a separate
write token that stay on the server; clients get in with an OAuth access token
instead, so neither Gitea token reaches Claude, ChatGPT or whatever else you connect.

## How the three repos fit together

- [nas-auth](https://github.com/ZhengchenTao/nas-auth): the authorization
  server. Signs users in and issues tokens.
- [obsidian-mcp](https://github.com/ZhengchenTao/obsidian-mcp): MCP server for
  an Obsidian vault (read and write).
- [gitea-mcp](https://github.com/ZhengchenTao/gitea-mcp): MCP server for a
  Gitea instance (read plus allowlisted writes).

An MCP client finds the authorization server through the MCP server's
metadata, registers itself, lets the user sign in, and gets a token that is
only valid for that one MCP server. The MCP servers check the token against
nas-auth's public keys. They never see a password or a shared secret. Each
repo also works on its own: the MCP servers accept tokens from any standard
OAuth server, and nas-auth can front any service that verifies JWTs.

## How a request gets in

```
MCP client (Claude, ChatGPT, Cursor …)
    │ 1. GET /.well-known/oauth-protected-resource   → which auth server to use
    │ 2. OAuth authorization code + PKCE              (against that server)
    │ 3. POST /mcp with Bearer <JWT>  (aud=gitea, scope read:gitea; writes also need write:gitea)
    ▼
gitea-mcp
    │ checks the JWT (RS256 via JWKS, or an HS256 shared key)
    │ reads with its read-only token; writes with a separate, optional write token
    ▼
Gitea REST API
```

## Tools

All tools need `scope=read:gitea`. Write tools additionally require `scope=write:gitea`.

| Tool | |
|---|---|
| `list_repos`, `read_repo` | Repositories the token can see (personal and org, public and private); topics, default branch, mirror flag |
| `list_tree`, `read_file` | File tree at a ref (up to 500 entries); raw file content, cut at `MaxFileBytes` |
| `search_code` | Code search, if the Gitea indexer is enabled |
| `list_branches`, `list_commits`, `read_commit` | Branches with their last commit; recent commits; one commit with per-file diffs (up to 50 files) |
| `list_issues`, `read_issue` | Issues by state; one issue with its comments |
| `list_pulls`, `read_pull` | Pull requests by state; one PR with review comments and changed files |
| `list_orgs`, `read_org` | Organizations |
| `list_packages`, `read_package` | Package registry by owner (container, generic, npm …); one version |
| `list_workflow_runs`, `read_run_log` | Actions runs; one run with its jobs and the tail of the log |
| `create_branch` | Create a working branch from a branch, tag, or commit SHA |
| `upsert_file`, `delete_file` | Create/update/delete one UTF-8 text file; every write requires `expectedHeadSha`, and updates/deletes also require the file blob `expectedSha` |
| `commit_files` | Create, update, and delete up to 20 files; requires expected branch HEAD and per-file SHAs for updates/deletes |
| `create_pull_request` | Open a PR from a working branch to a target branch |
| `get_diff` | Read one commit diff or compare two refs |

The `/mcp` endpoint keeps the existing `read:gitea` gate. A write call also checks `write:gitea`, an exact server-side repository allowlist and a separate write PAT. The allowlist is empty by default. Direct commits to `main`, `master` and the repository default branch are supported, with file SHA and branch HEAD checks. Gitea enforces the token's push permissions and native branch protection. `Gitea__WriteProtectedBranches` can additionally block specific branches and defaults to empty. File paths are relative and traversal segments are rejected. The server defaults to at most 20 files, 256 KiB per file, 1 MiB total file content, and an 8 MiB HTTP request body.

`merge_pull_request` merges a reviewed PR using a mandatory `expectedHeadSha` from `read_pull.head_sha`. Supported styles are `merge`, `squash`, `rebase`, `rebase-merge` and `fast-forward-only`. Existing CI must succeed; repositories without CI can merge. Gitea atomically checks `head_commit_id` and enforces required reviews and repository rules. The tool never force-merges or deletes the source branch, and never replays a merge after a timeout. Read the PR to resolve an unknown outcome. To commit directly to main, use `commit_files` with `branch=main` and its current `expectedHeadSha`.

Write errors are returned as actionable MCP tool errors: missing scope/PAT, disallowed repo/branch, stale file SHA, stale branch HEAD, and uncertain timeout outcomes have separate messages. For an uncertain outcome, inspect the branch/diff or PR before retrying.

Recommended workflow: inspect files → `create_branch` → use `list_tree` on that working branch for file blob SHAs and the `create_branch` result for its initial HEAD SHA → read content with `read_file` → `commit_files` → use its returned `commit_sha` as the next `expectedHeadSha` → `get_diff` → `create_pull_request` → review with `read_pull`.

Gitea 1.27.3's multi-file `POST /repos/{owner}/{repo}/contents` checks file SHAs but does not accept a branch HEAD precondition. The service stages one batch commit on a unique temporary ref, then uses `PUT /repos/{owner}/{repo}/branches/{branch}` with `old_commit_id=<expectedHeadSha>` and `force=false` to compare-and-swap the working branch. A stale HEAD leaves the target ref unchanged. Temporary refs are deleted after success or a known conflict; cleanup failures are returned as `cleanup_warning`. If a request times out and the target ref cannot be verified, the tool returns an explicit outcome-unknown error and avoids replaying the batch. Branch/PR create timeouts likewise require checking refs or existing head/base PRs before retrying. This implementation uses the [Gitea 1.27 multi-file API](https://docs.gitea.com/api/1.27/operations/repo-change-files/) and [conditional branch update API](https://docs.gitea.com/api/1.27/operations/repo-update-branch/).

## The Gitea token

Keep the existing read token under Gitea → Settings → Applications with only these scopes:
`read:repository`, `read:organization`, `read:package`, `read:issue`,
`read:user`. Nothing `write:` or `admin:`. The tools can only see what this
token can see, so a token from a normal user limits the server to that user's
repositories; `Gitea__RepoBlacklist` hides specific ones on top.

The existing `Gitea__AdminPat` is used by the read API client only. For writes,
configure a separate `Gitea__WritePat` for a non-admin Gitea account. Give that
account write access only to the intended repository (use a repository-restricted
token if the Gitea version supports it) and grant only `write:repository`.
Gitea's granular token scopes are available from v1.23. Never route write calls
through the existing read client or broadly share a write token. `Gitea__WritePat`
is optional; if it is empty, write tools are disabled. `Gitea__WriteRepoAllowlist`
must also name the exact repositories, for example `dev/room-organizer`.

If either token leaks, revoke it in Gitea and replace only the corresponding
`Gitea__AdminPat` or `Gitea__WritePat`, then restart. Before enabling writes,
verify that the instance API version matches the supported multi-file and branch-update endpoints above.

## Configuration

Environment variables, `__` for nesting.

| Variable | Default | |
|---|---|---|
| `Gitea__BaseUrl` | – | Gitea root URL, no trailing slash. Required. |
| `Gitea__AdminPat` | – | Credential for the read API client; write calls never use it. Required. |
| `Gitea__WritePat` | – | Separate least-privilege write token. Optional; blank disables writes. |
| `Gitea__RepoBlacklist` | – | Comma-separated `owner/repo` to hide |
| `Gitea__WriteRepoAllowlist` | empty | Exact comma-separated `owner/repo` entries allowed to write; empty denies all writes |
| `Gitea__WriteProtectedBranches` | Empty | Extra branch names that cannot receive direct commits; native Gitea protection still applies |
| `Gitea__DefaultLimit` | `50` | Default page size for list tools |
| `Gitea__MaxFileBytes` | `1048576` | Largest file or log read, in bytes |
| `Gitea__MaxWriteFiles` | `20` | Maximum operations in one `commit_files` call |
| `Gitea__MaxWriteFileBytes` | `262144` | Maximum UTF-8 bytes per written file |
| `Gitea__MaxWriteRequestBytes` | `1048576` | Maximum combined UTF-8 file content in one write |
| `Gitea__MaxMcpRequestBodyBytes` | `8388608` | Maximum inbound HTTP request body |
| `Jwt__Algorithm` | `HS256` | `RS256` (keys from the issuer's JWKS) or `HS256` (shared key) |
| `Jwt__Issuer` | – | Expected `iss`. Required. In RS256 mode keys are fetched from `<Issuer>/.well-known/openid-configuration`. |
| `Jwt__Audience` | `gitea` | Expected `aud` |
| `Jwt__ValidTypes__N` | – | RS256 only: allowed `typ` header values. Set `at+jwt` for nas-auth, whose id_tokens share the signing key. Empty means no check, for providers whose tokens say `typ: JWT`. |
| `Jwt__SigningKey__Current` / `__Previous` | – | HS256 only: the key shared with your auth server, and the previous one during rotation |
| `Mcp__OAuthDiscovery__Issuer` | – | Required. Published in this server's OAuth metadata. |
| `Mcp__OAuthDiscovery__AuthorizationEndpoint` | – | Required |
| `Mcp__OAuthDiscovery__TokenEndpoint` | – | Required |
| `Mcp__OAuthDiscovery__RegistrationEndpoint` | – | The auth server's `/register`, if it supports dynamic registration |
| `Mcp__OAuthDiscovery__ResourceUrl` | request host | The RFC 9728 `resource` value. Must match what the auth server expects. |

With nas-auth that comes down to:

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

and a `gitea` entry in nas-auth's `resources.json` with the same
`resource_url` and scopes `read:gitea` and `write:gitea`. First verify the
one-time migration marker `user_resources_seeded_v1=1`: an unseeded database
would grant all configured scopes to every existing user allowed by
`admin_only` on its first seed. After seeding, changes to `resources.json`
do not expand existing `user_resources` grants; explicitly grant
`read:gitea write:gitea` only to the intended user. `admin_only=true` still
rejects non-admin users even if they have a grant row, so set it to `false`
only when the intended user is non-admin. Restart nas-auth after changing
resource config, then reauthorize the MCP client. Access tokens must contain
both scopes because `/mcp` retains its existing `read:gitea` route gate.

## Which auth server

Claude and other remote-connector clients insist on the full OAuth flow:
authorization code with PKCE, discovered from this server's metadata. There is
no "paste a token" option. Whatever you use has to support PKCE, dynamic client
registration (so the client can register itself), the `resource` parameter
(RFC 8707) and custom scopes (`read:gitea`, plus `write:gitea` for authorized writing).

- [nas-auth](https://github.com/ZhengchenTao/nas-auth) is the one this server
  was written against. Small, self-hosted, RS256 mode as shown above.
- Hosted: [Logto](https://logto.io), [ZITADEL](https://zitadel.com),
  [Auth0](https://auth0.com). Use RS256 mode with your tenant's issuer URL.
- Self-hosted and bigger: [Keycloak](https://www.keycloak.org),
  [Authentik](https://goauthentik.io), or ZITADEL / Logto on your own box.
- HS256 mode is for a minimal auth server you write yourself; it needs the same
  key on both sides.

## Running it

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

For local development it's easiest to use HS256 and mint a token yourself:

```bash
export Gitea__BaseUrl=https://gitea.example.com
export Gitea__AdminPat=<read-only token>
export Jwt__Issuer=https://auth.example.com
export Jwt__SigningKey__Current=dev-secret-key-at-least-32-chars-long
export Mcp__OAuthDiscovery__Issuer=https://auth.example.com
export Mcp__OAuthDiscovery__AuthorizationEndpoint=https://auth.example.com/authorize
export Mcp__OAuthDiscovery__TokenEndpoint=https://auth.example.com/token
dotnet run

dotnet user-jwts create --issuer https://auth.example.com --audience gitea \
  --name tester --claim sub=tester --claim scope=read:gitea

npx @modelcontextprotocol/inspector
# Streamable HTTP, http://localhost:5000/mcp, paste the token as Bearer
```

Tests: `dotnet test gitea-mcp.Tests`.

For a real but opt-in HTTP/MCP integration run, see
[`Run-GiteaWriteIntegration.py`](gitea-mcp.Tests/Run-GiteaWriteIntegration.py).
It requires an exact allowlisted test repository (`zhengchen.tao/gitea-mcp-write-test`
or the existing `zhengchen.tao/gitea-pr-selftest` scratch repo), the same
`Gitea__AdminPat` credential used by the local read client, and a separate
non-admin `Gitea__WritePat` account with write access only to that test repo.
The AdminPat is used only for API reads; every mutating MCP operation uses
WritePat. The write PAT needs only `write:repository`. The test repository must
have a protected default branch and a separate protected non-default branch the
test account cannot bypass. Before writing, the script displays the target and
file plan and requires confirmation bound to the repository and branch. It leaves
the test branch and PR for review and never deletes the repository or test data.
Set `GITEA_TEST_INSTANCE`, `GITEA_TEST_OWNER`, `GITEA_TEST_REPO`,
`GITEA_TEST_ACCOUNT`, `GITEA_TEST_READ_PAT` (the read client's AdminPat),
`GITEA_TEST_WRITE_PAT`, and `GITEA_TEST_JWT_KEY` in the invoking environment.
The script starts a local server with a one-repository allowlist and does not
change nas-auth or deployment settings. Secret values are never printed. The
test repo must be private and non-archived. Run
`python .\gitea-mcp.Tests\Run-GiteaWriteIntegration.py` after building.

## CI

`.gitea/workflows/build-image.yml` builds and pushes
`<REGISTRY>/<IMAGE_OWNER>/gitea-mcp` on every push to `main` and can then
trigger a redeploy over SSH. It reads `vars.REGISTRY`, `vars.IMAGE_OWNER`,
`secrets.AIFACELY_REGISTRY_TOKEN`, and for the deploy step
`vars.DEPLOY_SERVICE`, `secrets.NAS_CI_SSH_KEY`, `secrets.NAS_SSH_HOST`,
`secrets.NAS_SSH_KNOWN_HOSTS`. The action URLs and build proxy match my own CI;
change them in a fork.

## License

MIT
