# gitea-mcp

Read-only access to a [Gitea](https://about.gitea.com/) instance over
[MCP (Model Context Protocol)](https://modelcontextprotocol.io/), gated by
OAuth-issued JWT bearer tokens.

The MCP server holds a single read-only Gitea Personal Access Token (PAT)
internally and never exposes it to the MCP client. Clients authenticate to
this server with an OAuth-issued JWT instead.

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

| Tool | Description |
|------|-------------|
| `list_repos` | List all repos visible to the PAT (personal + org, public + private) |
| `read_repo` | Repo metadata: topics, stars, default branch, mirror flag |
| `list_tree` | File tree at a ref (recursive optional, max 500 entries) |
| `read_file` | Raw file content (UTF-8, truncated at `MaxFileBytes`) |
| `search_code` | Code search via Gitea indexer (requires indexer enabled on the instance) |
| `list_branches` | Branch list + last commit per branch |
| `list_commits` | Recent commits with author + message |
| `read_commit` | Full commit details + per-file diff (max 50 files) |
| `list_issues` | Issues filtered by state (open/closed/all) |
| `read_issue` | Issue body + all comments |
| `list_pulls` | Pull requests filtered by state |
| `read_pull` | PR body + review comments + changed files |
| `list_orgs` | All organizations visible to the PAT |
| `read_org` | Org metadata |
| `list_packages` | Packages in registry by owner (container/generic/npm/...) |
| `read_package` | Package version metadata |
| `list_workflow_runs` | Gitea Actions workflow run history |
| `read_run_log` | Run details + job list + log (truncated at `MaxFileBytes`) |

All tools require a valid JWT with `scope=read:gitea`.

## Configuration

| Variable | Default | Required | Description |
|----------|---------|---|-------------|
| `Gitea__BaseUrl` | — | **yes** | Gitea root URL, no trailing slash |
| `Gitea__AdminPat` | — | **yes** | Gitea read-only PAT — see PAT setup below |
| `Gitea__RepoBlacklist` | *(empty)* | no | Comma-separated `owner/repo` pairs to hide |
| `Gitea__DefaultLimit` | `50` | no | Default page size for list operations |
| `Gitea__MaxFileBytes` | `1048576` | no | Max file/log read size in bytes (1MB) |
| `Jwt__Issuer` | — | **yes** | Expected `iss` claim — your AS's issuer URL |
| `Jwt__Audience` | `gitea` | no | Expected `aud` claim |
| `Jwt__SigningKey__Current` | — | **yes** | HS256 signing key, shared with your AS |
| `Jwt__SigningKey__Previous` | — | no | Previous key for rotation window |
| `Mcp__OAuthDiscovery__Issuer` | — | **yes** | `/.well-known` `issuer` field |
| `Mcp__OAuthDiscovery__AuthorizationEndpoint` | — | **yes** | Your AS's `/authorize` URL |
| `Mcp__OAuthDiscovery__TokenEndpoint` | — | **yes** | Your AS's `/token` URL |
| `Mcp__OAuthDiscovery__RegistrationEndpoint` | — | no | Your AS's `/register` URL (DCR) |
| `Mcp__OAuthDiscovery__ResourceUrl` | request host | no | RFC 9728 `resource` identifier |
| `ASPNETCORE_ENVIRONMENT` | `Production` | no | `Development` for verbose logs |

Configuration keys use the ASP.NET Core double-underscore convention for
nesting (`Jwt__SigningKey__Current` ⇔ `Jwt:SigningKey:Current`).

## PAT setup (Gitea → Settings → Applications)

Generate a token with **only these scopes** (principle of least privilege):

- `read:repository`
- `read:organization`
- `read:package`
- `read:issue`
- `read:user`

Do NOT grant `write:*` or `admin:*` scopes.

If the PAT is ever compromised: revoke in Gitea → generate a new one → update
`Gitea__AdminPat` env → restart the container.

## Local development

```bash
# 1. Restore and configure
dotnet restore
export Gitea__BaseUrl=https://gitea.example.com
export Gitea__AdminPat=<your read-only PAT>
export Jwt__Issuer=https://your-auth-server.example.com
export Jwt__SigningKey__Current=dev-secret-key-at-least-32-chars-long
export Mcp__OAuthDiscovery__Issuer=https://your-auth-server.example.com
export Mcp__OAuthDiscovery__AuthorizationEndpoint=https://your-auth-server.example.com/authorize
export Mcp__OAuthDiscovery__TokenEndpoint=https://your-auth-server.example.com/token

dotnet run
# Listens on http://localhost:5000

# 2. Generate a dev JWT
dotnet user-jwts create \
  --issuer https://your-auth-server.example.com \
  --audience gitea \
  --name tester \
  --claim sub=tester \
  --claim scope=read:gitea

# 3. Test with MCP Inspector
npx @modelcontextprotocol/inspector
# Transport: Streamable HTTP
# URL: http://localhost:5000/mcp
# Bearer Token: <token from step 2>

# 4. Run unit tests
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

The included `.gitea/workflows/build-image.yml` builds and pushes the image
on every push to `main`. It expects these repository Variables / Secrets:

- `vars.REGISTRY` — registry hostname (e.g. `ghcr.io`)
- `vars.IMAGE_OWNER` — registry owner/namespace
- `secrets.PACKAGES_TOKEN` — registry push token

## License

MIT
