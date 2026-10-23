using GiteaMcp.Auth;
using GiteaMcp.Config;
using GiteaMcp.Endpoints;
using GiteaMcp.Services;
using Microsoft.Extensions.Http.Resilience;
using System.Net;
using System.Net.Http.Headers;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

var maxMcpRequestBodyBytes = builder.Configuration.GetValue<long>("Gitea:MaxMcpRequestBodyBytes", 8 * 1024 * 1024);
if (maxMcpRequestBodyBytes < 1024)
    throw new InvalidOperationException("Gitea:MaxMcpRequestBodyBytes must be at least 1024 bytes.");
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = maxMcpRequestBodyBytes);

// ─── 配置绑定 ───────────────────────────────────────────────
builder.Services.Configure<GiteaOptions>(
    builder.Configuration.GetSection(GiteaOptions.SectionName));
builder.Services.Configure<JwtOptions>(
    builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<McpDiscoveryOptions>(
    builder.Configuration.GetSection(McpDiscoveryOptions.SectionName));

// ─── JWT Bearer + Scope Policy ─────────────────────────────
builder.Services.AddGiteaJwtBearer(builder.Configuration);
builder.Services.AddScopePolicies();

// ─── HTTP Context Accessor（Tool 里可选用，暂保留接口） ────────
builder.Services.AddHttpContextAccessor();

// ─── Gitea HTTP Client ─────────────────────────────────────
var giteaBaseUrl = builder.Configuration["Gitea:BaseUrl"];
if (string.IsNullOrWhiteSpace(giteaBaseUrl))
    throw new InvalidOperationException(
        "Gitea:BaseUrl 未配置。请通过 env Gitea__BaseUrl 设置你的 Gitea 实例 URL。");
var giteaPat = builder.Configuration["Gitea:AdminPat"] ?? string.Empty;

builder.Services.AddHttpClient("gitea", client =>
{
    // 确保 BaseAddress 末尾有斜杠（HttpClient 的规范）
    var url = giteaBaseUrl.TrimEnd('/') + "/";
    client.BaseAddress = new Uri(url);

    // Gitea 推荐 "token <PAT>" 格式，比 Bearer 更稳
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("token", giteaPat);

    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.UserAgent.ParseAdd("gitea-mcp/1.0");

    // 单请求超时 30s
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddStandardResilienceHandler(options =>
{
    // 3 次重试，指数退避（Microsoft.Extensions.Http.Resilience 标准配置）
    options.Retry.MaxRetryAttempts = 3;
    options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
    options.Retry.Delay = TimeSpan.FromSeconds(1);
    options.Retry.UseJitter = true;
    // 仅对 5xx / 429 / 网络错误重试；4xx 由 ShouldHandle 默认配置自动跳过
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
    // Polly 校验：SamplingDuration ≥ 2× AttemptTimeout，默认 30s 不达标
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
});

// 写请求单独使用专用 PAT，且不启用自动重试：上游已提交但响应丢失时自动重试可能重复创建提交或 PR。
builder.Services.AddHttpClient("gitea-write", client =>
{
    var url = giteaBaseUrl.TrimEnd('/') + "/";
    client.BaseAddress = new Uri(url);
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("token", builder.Configuration["Gitea:WritePat"] ?? string.Empty);
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.UserAgent.ParseAdd("gitea-mcp/1.0");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ─── 业务服务 ──────────────────────────────────────────────
builder.Services.AddSingleton<GiteaRepoFilter>();
builder.Services.AddScoped<GiteaWritePolicy>();
builder.Services.AddScoped<GiteaApiClient>();

// ─── MCP Server ────────────────────────────────────────────
var instanceMetadata = new McpInstanceMetadata(giteaBaseUrl,
    builder.Configuration.GetSection(McpInstanceOptions.SectionName).Get<McpInstanceOptions>()
        ?? new McpInstanceOptions());
builder.Services.AddMcpServer()
    .WithHttpTransport()          // Streamable HTTP（Claude.ai custom connector 走这个）
    .WithToolsFromAssembly();     // 自动扫描 [McpServerToolType]
builder.Services.PostConfigure<McpServerOptions>(instanceMetadata.Configure);

// ─── Build ─────────────────────────────────────────────────
var app = builder.Build();

// ─── Middleware 顺序：认证 → 授权 → 路由 ────────────────────
app.UseAuthentication();
app.UseAuthorization();

// ─── Endpoints ─────────────────────────────────────────────
app.MapDiscovery();

// MCP 端点：要求通过 JWT 认证 + read:gitea scope
app.MapMcp("/mcp")
   .RequireAuthorization(ScopePolicies.ReadGitea);

// 健康检查（Kubernetes / Docker healthcheck 用）
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", timestamp = DateTimeOffset.UtcNow }));

app.Run();
