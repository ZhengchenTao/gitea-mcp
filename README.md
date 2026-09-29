# gitea-mcp

English | [简体中文](README.zh-CN.md)

An [MCP](https://modelcontextprotocol.io/) server that gives an AI client
read-only access to a [Gitea](https://about.gitea.com/) instance: repositories,
files, commits, issues, pull requests, packages and Actions runs. It talks to
Gitea with one read-only personal access token that stays on the server;
clients get in with an OAuth access token instead, so the Gitea token never
reaches Claude, ChatGPT or whatever else you connect.

## How the three repos fit together

- [nas-auth](https://github.com/ZhengchenTao/nas-auth): the authorization
  server. Signs users in and issues tokens.
- [obsidian-mcp](https://github.com/ZhengchenTao/obsidian-mcp): MCP server for
  an Obsidian vault (read and write).
- [gitea-mcp](https://github.com/ZhengchenTao/gitea-mcp): MCP server for a
  Gitea instance (read-only).

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
    │ 3. POST /mcp with Bearer <JWT>  (aud=gitea, scope read:gitea)
    ▼
gitea-mcp
    │ checks the JWT (RS256 via JWKS, or an HS256 shared key)
    │ calls Gitea with its own read-only token
    ▼
Gitea REST API
```

## Tools

All of them need `scope=read:gitea`.

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

## The Gitea token

Create it under Gitea → Settings → Applications with only these scopes:
`read:repository`, `read:organization`, `read:package`, `read:issue`,
`read:user`. Nothing `write:` or `admin:`. The tools can only see what this
token can see, so a token from a normal user limits the server to that user's
repositories; `Gitea__RepoBlacklist` hides specific ones on top.

If it leaks: revoke it in Gitea, create a new one, update `Gitea__AdminPat`,
restart.

## Configuration

Environment variables, `__` for nesting.

| Variable | Default | |
|---|---|---|
| `Gitea__BaseUrl` | – | Gitea root URL, no trailing slash. Required. |
| `Gitea__AdminPat` | – | The read-only token above. Required. |
| `Gitea__RepoBlacklist` | – | Comma-separated `owner/repo` to hide |
| `Gitea__DefaultLimit` | `50` | Default page size for list tools |
| `Gitea__MaxFileBytes` | `1048576` | Largest file or log read, in bytes |
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
`resource_url` and the scope `read:gitea`.

## Which auth server

Claude and other remote-connector clients insist on the full OAuth flow:
authorization code with PKCE, discovered from this server's metadata. There is
no "paste a token" option. Whatever you use has to support PKCE, dynamic client
registration (so the client can register itself), the `resource` parameter
(RFC 8707) and a custom scope (`read:gitea`).

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
