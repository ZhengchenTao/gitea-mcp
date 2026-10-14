using GiteaMcp.Config;
using GiteaMcp.Services;
using GiteaMcp.Services.Models;
using GiteaMcp.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GiteaMcp.Tests;

public sealed class GiteaWriteToolsTests
{
    private const string RepoUrl = "/api/v1/repos/dev/room-organizer";
    private const string ExistingSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string InitialHeadSha = new('c', 40);
    private static readonly string StagedCommitSha = new('d', 40);
    private static readonly string ConcurrentHeadSha = new('e', 40);

    [Fact]
    public async Task ReadOnlyToken_CannotCallWriteTool()
    {
        var env = CreateEnvironment("read:gitea");

        var error = await Assert.ThrowsAsync<McpException>(() =>
            env.Tools.create_branch("dev", "room-organizer", "feature/test", "main"));

        Assert.Contains("write:gitea", error.Message);
        Assert.Empty(env.Handler.Mutations);
    }

    [Fact]
    public async Task MissingWritePat_DisablesWritesWithoutFallbackToReadPat()
    {
        var env = CreateEnvironment("read:gitea write:gitea", writePat: "");

        var error = await Assert.ThrowsAsync<McpException>(() =>
            env.Tools.create_branch("dev", "room-organizer", "feature/test", "main"));

        Assert.Contains("WritePat is not configured", error.Message);
        Assert.Empty(env.Handler.Requests);
    }

    [Fact]
    public async Task WritePatForbidden_IsReportedWithoutRetryingWithReadPat()
    {
        var env = CreateEnvironment("read:gitea write:gitea", rejectWrites: true);

        await Assert.ThrowsAsync<McpException>(() =>
            env.Tools.create_branch("dev", "room-organizer", "feature/test", "main"));

        var writeRequest = Assert.Single(env.Handler.Mutations);
        Assert.Equal("token write-test-token", writeRequest.Authorization);
    }

    [Fact]
    public async Task ExistingBranch_ReturnsExplicitConflict()
    {
        var env = CreateEnvironment("read:gitea write:gitea", existingBranch: true);

        var error = await Assert.ThrowsAsync<McpException>(() =>
            env.Tools.create_branch("dev", "room-organizer", "feature/already-there", "main"));

        Assert.Contains("already exists", error.Message);
        Assert.Empty(env.Handler.Mutations);
    }

    [Fact]
    public async Task CreateBranch_AcceptsCommitShaAsSourceRef()
    {
        var env = CreateEnvironment("read:gitea write:gitea");
        var sourceSha = new string('c', 40);

        await env.Tools.create_branch("dev", "room-organizer", "feature/from-commit", sourceSha);

        using var payload = JsonDocument.Parse(env.Handler.Mutations.Single().Body!);
        Assert.Equal(sourceSha, payload.RootElement.GetProperty("old_ref_name").GetString());
    }

    [Theory]
    [InlineData("dev", "room-organizer")]
    [InlineData("zhengchen.tao", "gitea-mcp")]
    [InlineData("another-owner", "another-repo")]
    public void AnyRepository_IsAllowedWithWriteScopeAndPat(string owner, string repo)
    {
        var env = CreateEnvironment("read:gitea write:gitea");
        env.Policy.RequireWriteAccess(owner, repo);
        Assert.Empty(env.Handler.Requests);
    }

    [Fact]
    public async Task DefaultBranch_AllowsDirectCommitWithHeadCompareAndSwap()
    {
        var env = CreateEnvironment("read:gitea write:gitea");

        await env.Tools.commit_files("dev", "room-organizer", "main", InitialHeadSha, "direct main", new[]
            {
                new GiteaWriteFileChange { Operation = "create", Path = "new.txt", Content = "x" },
            });

        Assert.Equal(StagedCommitSha, env.Handler.TargetHeadSha);
        Assert.Contains(env.Handler.Mutations, x => x.Method == "PUT" && x.Path == RepoUrl + "/branches/main");
    }

    [Fact]
    public async Task GiteaProtectedBranch_UpstreamRejectionIsPreservedAndStagingIsCleaned()
    {
        var env = CreateEnvironment("read:gitea write:gitea", protectedBranch: true);

        await Assert.ThrowsAsync<McpException>(() =>
            env.Tools.commit_files("dev", "room-organizer", "release/secure", InitialHeadSha, "unsafe", new[]
            {
                new GiteaWriteFileChange { Operation = "create", Path = "new.txt", Content = "x" },
            }));

        Assert.Contains(env.Handler.Mutations, x => x.Method == "PUT");
        Assert.Equal(InitialHeadSha, env.Handler.TargetHeadSha);
        Assert.Null(env.Handler.StagingBranch);
    }

    [Fact]
    public async Task CreateBranch_CommitFiles_AndCreatePullRequest_Succeed()
    {
        var env = CreateEnvironment("read:gitea write:gitea");

        var branch = await env.Tools.create_branch("dev", "room-organizer", "feature/room-yaml", "main");
        var commit = await env.Tools.commit_files("dev", "room-organizer", "feature/room-yaml", InitialHeadSha, "Update room settings", new[]
        {
            new GiteaWriteFileChange { Operation = "create", Path = "data/room.yaml", Content = "rooms: []\n" },
            new GiteaWriteFileChange { Operation = "update", Path = "README.md", Content = "updated\n", ExpectedSha = ExistingSha },
            new GiteaWriteFileChange { Operation = "delete", Path = "old.txt", ExpectedSha = new string('b', 40) },
        });
        var pull = await env.Tools.create_pull_request(
            "dev", "room-organizer", "feature/room-yaml", "main", "Update room settings", "Review the YAML changes.");

        Assert.NotNull(branch);
        Assert.NotNull(commit);
        Assert.NotNull(pull);
        Assert.Equal("POST " + RepoUrl + "/branches", env.Handler.Mutations[0].Method + " " + env.Handler.Mutations[0].Path);
        Assert.Equal("POST " + RepoUrl + "/contents", env.Handler.Mutations[1].Method + " " + env.Handler.Mutations[1].Path);
        Assert.StartsWith("PUT " + RepoUrl + "/branches/feature", env.Handler.Mutations[2].Method + " " + env.Handler.Mutations[2].Path);
        Assert.StartsWith("DELETE " + RepoUrl + "/branches/mcp-tmp-", env.Handler.Mutations[3].Method + " " + env.Handler.Mutations[3].Path);
        Assert.Equal("POST " + RepoUrl + "/pulls", env.Handler.Mutations[4].Method + " " + env.Handler.Mutations[4].Path);

        using var payload = JsonDocument.Parse(env.Handler.BatchBody!);
        var files = payload.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(3, files.Length);
        Assert.Equal("create", files[0].GetProperty("operation").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("rooms: []\n")), files[0].GetProperty("content").GetString());
        Assert.Equal(ExistingSha, files[1].GetProperty("sha").GetString());
        Assert.Equal("delete", files[2].GetProperty("operation").GetString());
        Assert.Equal(new string('b', 40), files[2].GetProperty("sha").GetString());
        Assert.False(payload.RootElement.TryGetProperty("force_push", out _));
        using var cas = JsonDocument.Parse(env.Handler.Mutations[2].Body!);
        Assert.Equal(InitialHeadSha, cas.RootElement.GetProperty("old_commit_id").GetString());
        Assert.Equal(StagedCommitSha, cas.RootElement.GetProperty("new_commit_id").GetString());
        Assert.False(cas.RootElement.GetProperty("force").GetBoolean());
        Assert.Equal(StagedCommitSha, env.Handler.TargetHeadSha);
        Assert.Null(env.Handler.StagingBranch);
    }

    [Fact]
    public async Task StaleFileSha_IsRejectedWithoutPostingChanges()
    {
        var env = CreateEnvironment("read:gitea write:gitea");

        var error = await Assert.ThrowsAsync<McpException>(() =>
            env.Tools.commit_files("dev", "room-organizer", "feature/test", InitialHeadSha, "stale update", new[]
            {
                new GiteaWriteFileChange
                {
                    Operation = "update", Path = "README.md", Content = "stale", ExpectedSha = new string('b', 40),
                },
            }));

        Assert.Contains("SHA conflict", error.Message);
        Assert.Empty(env.Handler.Mutations);
    }

    [Fact]
    public async Task FailedMultiFileBatch_UsesOneGiteaMutationAndNoPerFileWrites()
    {
        var env = CreateEnvironment("read:gitea write:gitea", failBatch: true);

        await Assert.ThrowsAsync<McpException>(() =>
            env.Tools.commit_files("dev", "room-organizer", "feature/test", InitialHeadSha, "atomic attempt", new[]
            {
                new GiteaWriteFileChange { Operation = "create", Path = "first.yaml", Content = "first" },
                new GiteaWriteFileChange { Operation = "update", Path = "README.md", Content = "new", ExpectedSha = ExistingSha },
            }));

        Assert.Single(env.Handler.Mutations);
        Assert.Equal("POST", env.Handler.Mutations[0].Method);
        Assert.Equal(RepoUrl + "/contents", env.Handler.Mutations[0].Path);
        using var payload = JsonDocument.Parse(env.Handler.BatchBody!);
        Assert.Equal(2, payload.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal(InitialHeadSha, env.Handler.TargetHeadSha);
        Assert.Null(env.Handler.StagingBranch);
    }

    [Fact]
    public async Task StaleExpectedHead_IsRejectedBeforeStaging()
    {
        var env = CreateEnvironment("read:gitea write:gitea");

        var error = await Assert.ThrowsAsync<McpException>(() =>
            env.Tools.commit_files("dev", "room-organizer", "feature/test", new string('f', 40), "stale head", new[]
            {
                new GiteaWriteFileChange { Operation = "create", Path = "new.yaml", Content = "x" },
            }));

        Assert.Contains("Branch HEAD conflict", error.Message);
        Assert.Empty(env.Handler.Mutations);
    }

    [Fact]
    public async Task HeadMovementBetweenBatchAndCas_DoesNotOverwriteNewerHead()
    {
        var env = CreateEnvironment("read:gitea write:gitea", moveHeadBeforeCas: true);

        var error = await Assert.ThrowsAsync<McpException>(() =>
            env.Tools.commit_files("dev", "room-organizer", "feature/test", InitialHeadSha, "race attempt", new[]
            {
                new GiteaWriteFileChange { Operation = "create", Path = "new.yaml", Content = "x" },
            }));

        Assert.Contains("Branch HEAD conflict", error.Message);
        Assert.Equal(ConcurrentHeadSha, env.Handler.TargetHeadSha);
        Assert.Null(env.Handler.StagingBranch);
    }

    [Fact]
    public async Task LostBatchResponse_RecoversStagedCommitWithoutReplayingBatch()
    {
        var env = CreateEnvironment("read:gitea write:gitea", loseBatchResponse: true);

        var result = await env.Tools.commit_files("dev", "room-organizer", "feature/test", InitialHeadSha, "recover batch", new[]
        {
            new GiteaWriteFileChange { Operation = "create", Path = "recovered.txt", Content = "one batch" },
        });

        Assert.Contains(StagedCommitSha, JsonSerializer.Serialize(result));
        Assert.Single(env.Handler.Mutations, m => m.Method == "POST" && m.Path == RepoUrl + "/contents");
        Assert.Equal(StagedCommitSha, env.Handler.TargetHeadSha);
        Assert.Null(env.Handler.StagingBranch);
    }

    [Fact]
    public async Task LostCasResponse_IsResolvedByReadingCurrentHead()
    {
        var env = CreateEnvironment("read:gitea write:gitea", loseCasResponseAfterUpdate: true);

        await env.Tools.commit_files("dev", "room-organizer", "feature/test", InitialHeadSha, "recover CAS", new[]
        {
            new GiteaWriteFileChange { Operation = "create", Path = "cas-recovered.txt", Content = "one commit" },
        });

        Assert.Single(env.Handler.Mutations, m => m.Method == "POST" && m.Path == RepoUrl + "/contents");
        Assert.Single(env.Handler.Mutations, m => m.Method == "PUT" && m.Path.StartsWith(RepoUrl + "/branches/", StringComparison.Ordinal));
        Assert.Equal(StagedCommitSha, env.Handler.TargetHeadSha);
        Assert.Null(env.Handler.StagingBranch);
    }

    [Fact]
    public async Task LostCasResponseBeforeUpdate_RetriesOnlyConditionalRefUpdate()
    {
        var env = CreateEnvironment("read:gitea write:gitea", loseCasResponseBeforeUpdate: true);

        await env.Tools.commit_files("dev", "room-organizer", "feature/test", InitialHeadSha, "retry CAS", new[]
        {
            new GiteaWriteFileChange { Operation = "create", Path = "cas-retry.txt", Content = "one batch" },
        });

        Assert.Single(env.Handler.Mutations, m => m.Method == "POST" && m.Path == RepoUrl + "/contents");
        Assert.Equal(2, env.Handler.Mutations.Count(m => m.Method == "PUT" && m.Path.StartsWith(RepoUrl + "/branches/", StringComparison.Ordinal)));
        Assert.Equal(StagedCommitSha, env.Handler.TargetHeadSha);
        Assert.Null(env.Handler.StagingBranch);
    }

    [Fact]
    public async Task TraversalPath_IsRejectedBeforeGiteaWrite()
    {
        var env = CreateEnvironment("read:gitea write:gitea");

        await Assert.ThrowsAsync<McpException>(() =>
            env.Tools.upsert_file("dev", "room-organizer", "../outside.yaml", "no", "feature/test", InitialHeadSha));

        Assert.Empty(env.Handler.Mutations);
    }

    [Fact]
    public void FilePathValidation_RejectsEncodedTraversalAndAllowsOrdinarySpaces()
    {
        GiteaWritePolicy.ValidateFilePath("data/room settings.yaml");
        Assert.Throws<ArgumentException>(() => GiteaWritePolicy.ValidateFilePath("data%2F..%2Foutside.yaml"));
        Assert.Throws<ArgumentException>(() => GiteaWritePolicy.ValidateFilePath(".git/config"));
    }

    [Fact]
    public async Task ExistingReadApi_StillWorksWithReadOnlyToken()
    {
        var env = CreateEnvironment("read:gitea");

        var repo = await env.Api.GetRepoAsync("dev", "room-organizer");

        Assert.Equal("main", repo.DefaultBranch);
        Assert.Empty(env.Handler.Mutations);
    }

    [Fact]
    public async Task MergePullRequest_UsesReviewedHeadAndNeverForcesOrDeletes()
    {
        var env = CreateEnvironment("read:gitea write:gitea");
        await env.Tools.merge_pull_request("dev", "room-organizer", 42, InitialHeadSha, "squash");
        var request = Assert.Single(env.Handler.Mutations);
        Assert.Equal("write-test-token", request.Authorization!.Split(' ')[1]);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("squash", body.RootElement.GetProperty("do").GetString());
        Assert.Equal(InitialHeadSha, body.RootElement.GetProperty("head_commit_id").GetString());
        Assert.False(body.RootElement.GetProperty("force_merge").GetBoolean());
        Assert.False(body.RootElement.GetProperty("delete_branch_after_merge").GetBoolean());
    }

    [Fact]
    public async Task MergePullRequest_StaleHeadIsRejectedBeforeMutation()
    {
        var env = CreateEnvironment("read:gitea write:gitea");
        await Assert.ThrowsAsync<McpException>(() => env.Tools.merge_pull_request("dev", "room-organizer", 42, ConcurrentHeadSha));
        Assert.Empty(env.Handler.Mutations);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("pending")]
    [InlineData("error")]
    public async Task MergePullRequest_UnsuccessfulCiIsRejected(string state)
    {
        var env = CreateEnvironment("read:gitea write:gitea");
        env.Handler.CommitStatusState = state;
        await Assert.ThrowsAsync<McpException>(() => env.Tools.merge_pull_request("dev", "room-organizer", 42, InitialHeadSha));
        Assert.Empty(env.Handler.Mutations);
    }

    [Fact]
    public async Task MergePullRequest_StillRequiresWriteScope()
    {
        var env = CreateEnvironment("read:gitea");
        await Assert.ThrowsAsync<McpException>(() => env.Tools.merge_pull_request("dev", "room-organizer", 42, InitialHeadSha));
        Assert.Empty(env.Handler.Requests);
    }

    [Fact]
    public async Task MergePullRequest_LostResponseRecoversWithoutReplayingMerge()
    {
        var env = CreateEnvironment("read:gitea write:gitea");
        env.Handler.LoseMergeResponse = true;
        await env.Tools.merge_pull_request("dev", "room-organizer", 42, InitialHeadSha);
        Assert.Single(env.Handler.Mutations);
    }

    [Fact]
    public async Task MergePullRequest_RepositoryProtectionRejectionIsNotBypassed()
    {
        var env = CreateEnvironment("read:gitea write:gitea");
        env.Handler.MergeRejected = true;
        var error = await Assert.ThrowsAsync<McpException>(() => env.Tools.merge_pull_request("dev", "room-organizer", 42, InitialHeadSha));
        Assert.Contains("Gitea rejected", error.Message);
        Assert.Single(env.Handler.Mutations);
    }

    private static TestEnvironment CreateEnvironment(
        string scopes,
        bool protectedBranch = false,
        bool failBatch = false,
        bool existingBranch = false,
        bool moveHeadBeforeCas = false,
        string writePat = "write-test-token",
        bool rejectWrites = false,
        bool loseBatchResponse = false,
        bool loseCasResponseAfterUpdate = false,
        bool loseCasResponseBeforeUpdate = false)
    {
        var options = Options.Create(new GiteaOptions
        {
            BaseUrl = "https://gitea.test",
            AdminPat = "read-test-token",
            WritePat = writePat,
            MaxWriteFiles = 20,
            MaxWriteFileBytes = 256 * 1024,
            MaxWriteRequestBytes = 1024 * 1024,
        });
        var handler = new FakeGiteaHandler(
            protectedBranch, failBatch, existingBranch, moveHeadBeforeCas, rejectWrites,
            loseBatchResponse, loseCasResponseAfterUpdate, loseCasResponseBeforeUpdate);
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("scope", scopes) }, "Bearer")),
            },
        };
        var filter = new GiteaRepoFilter(options);
        var policy = new GiteaWritePolicy(accessor, options, filter);
        var factory = new FakeHttpClientFactory(handler);
        var api = new GiteaApiClient(factory, options, NullLogger<GiteaApiClient>.Instance, policy);
        return new TestEnvironment(api, new WriteTools(api, filter, options), handler, policy);
    }

    private sealed record TestEnvironment(GiteaApiClient Api, WriteTools Tools, FakeGiteaHandler Handler, GiteaWritePolicy Policy);

    private sealed class FakeHttpClientFactory(FakeGiteaHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://gitea.test/"),
            };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "token", name == "gitea-write" ? "write-test-token" : "read-test-token");
            return client;
        }
    }

    private sealed class FakeGiteaHandler(
        bool protectedBranch, bool failBatch, bool existingBranch, bool moveHeadBeforeCas,
        bool rejectWrites, bool loseBatchResponse, bool loseCasResponseAfterUpdate,
        bool loseCasResponseBeforeUpdate) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public List<CapturedRequest> Mutations { get; } = [];
        public string? BatchBody { get; private set; }
        public string TargetHeadSha { get; private set; } = InitialHeadSha;
        private string? _targetBranchName;
        public string? StagingBranch { get; private set; }
        private int _casAttempts;
        public string CommitStatusState { get; set; } = "success";
        public bool LoseMergeResponse { get; set; }
        public bool MergeRejected { get; set; }
        private bool _merged;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method.Method;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var captured = new CapturedRequest(method, path, body, request.Headers.Authorization?.ToString());
            Requests.Add(captured);
            if (method != "GET") Mutations.Add(captured);

            if (method == "GET" && path == RepoUrl + "/pulls/42")
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    number = 42, state = _merged ? "closed" : "open", merged = _merged,
                    head = new { sha = InitialHeadSha }, merge_commit_sha = _merged ? StagedCommitSha : null,
                }));
            if (method == "GET" && path.EndsWith("/status", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { state = CommitStatusState, total_count = 1 }));
            if (method == "POST" && path == RepoUrl + "/pulls/42/merge")
            {
                if (MergeRejected) return Json(HttpStatusCode.MethodNotAllowed, "{}");
                _merged = true;
                if (LoseMergeResponse) throw new HttpRequestException("simulated lost merge response");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (method == "GET" && path == RepoUrl)
                return Json(HttpStatusCode.OK, """{"default_branch":"main","html_url":"https://gitea.test/dev/room-organizer"}""");
            if (method == "GET" && path == RepoUrl + "/branches")
            {
                var payload = existingBranch
                    ? """[{"name":"feature/already-there","protected":false,"commit":{"id":"parent-sha"}}]"""
                    : "[]";
                return Json(HttpStatusCode.OK, payload);
            }
            if (method == "GET" && path.StartsWith(RepoUrl + "/branches/", StringComparison.Ordinal))
            {
                var branchName = Uri.UnescapeDataString(path[(RepoUrl.Length + "/branches/".Length)..]);
                if (branchName.StartsWith("mcp-tmp-", StringComparison.Ordinal))
                {
                    if (StagingBranch is null)
                        return Json(HttpStatusCode.NotFound, "{}");
                    return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                    {
                        name = StagingBranch,
                        @protected = false,
                        commit = new { id = StagedCommitSha },
                    }));
                }
                var payload = JsonSerializer.Serialize(new
                {
                    name = branchName,
                    @protected = protectedBranch && branchName == "release/secure",
                    commit = new { id = TargetHeadSha },
                });
                return Json(HttpStatusCode.OK, payload);
            }
            if (method == "GET" && path.StartsWith(RepoUrl + "/git/commits/", StringComparison.Ordinal))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    sha = StagedCommitSha,
                    commit = new { message = "recover batch" },
                    parents = new[] { new { sha = InitialHeadSha } },
                });
                return Json(HttpStatusCode.OK, payload);
            }
            if (method == "GET" && path == RepoUrl + "/contents-ext/README.md")
            {
                var payload = JsonSerializer.Serialize(new
                {
                    file_contents = new { path = "README.md", sha = ExistingSha, size = 1, type = "file" },
                });
                return Json(HttpStatusCode.OK, payload);
            }
            if (method == "GET" && path == RepoUrl + "/contents-ext/old.txt")
            {
                var payload = JsonSerializer.Serialize(new
                {
                    file_contents = new { path = "old.txt", sha = new string('b', 40), size = 1, type = "file" },
                });
                return Json(HttpStatusCode.OK, payload);
            }
            if (method == "GET" && path.StartsWith(RepoUrl + "/contents-ext/", StringComparison.Ordinal))
                return Json(HttpStatusCode.NotFound, "{} ");

            if (method == "POST" && path == RepoUrl + "/branches")
            {
                if (rejectWrites) return Json(HttpStatusCode.Forbidden, "{}");
                using var payload = JsonDocument.Parse(body!);
                _targetBranchName = payload.RootElement.GetProperty("new_branch_name").GetString();
                TargetHeadSha = InitialHeadSha;
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(new
                {
                    name = _targetBranchName,
                    @protected = false,
                    commit = new { id = InitialHeadSha },
                }));
            }
            if (method == "POST" && path == RepoUrl + "/contents")
            {
                if (rejectWrites) return Json(HttpStatusCode.Forbidden, "{}");
                BatchBody = body;
                if (failBatch) return Json(HttpStatusCode.Conflict, "{\"message\":\"stale SHA\"}");
                using var payload = JsonDocument.Parse(body!);
                StagingBranch = payload.RootElement.GetProperty("new_branch").GetString();
                var response = Json(HttpStatusCode.Created, JsonSerializer.Serialize(new
                {
                    commit = new
                    {
                        sha = StagedCommitSha,
                        html_url = "https://gitea.test/commit/" + StagedCommitSha,
                        message = payload.RootElement.GetProperty("message").GetString(),
                        parents = new[] { new { sha = InitialHeadSha } },
                    },
                }));
                if (loseBatchResponse) throw new HttpRequestException("simulated lost batch response");
                return response;
            }
            if (method == "PUT" && path.StartsWith(RepoUrl + "/branches/", StringComparison.Ordinal))
            {
                _casAttempts++;
                if (protectedBranch && path.EndsWith("release%2Fsecure", StringComparison.OrdinalIgnoreCase))
                    return Json(HttpStatusCode.Forbidden, "{}");
                if (rejectWrites) return Json(HttpStatusCode.Forbidden, "{}");
                if (moveHeadBeforeCas)
                {
                    TargetHeadSha = ConcurrentHeadSha;
                    return Json(HttpStatusCode.UnprocessableEntity, "{}");
                }
                using var payload = JsonDocument.Parse(body!);
                var expected = payload.RootElement.GetProperty("old_commit_id").GetString();
                if (!string.Equals(expected, TargetHeadSha, StringComparison.OrdinalIgnoreCase))
                    return Json(HttpStatusCode.UnprocessableEntity, "{}");
                if (loseCasResponseBeforeUpdate && _casAttempts == 1)
                    throw new HttpRequestException("simulated lost CAS response before update");
                TargetHeadSha = payload.RootElement.GetProperty("new_commit_id").GetString()!;
                if (loseCasResponseAfterUpdate)
                    throw new HttpRequestException("simulated lost CAS response after update");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (method == "DELETE" && path.StartsWith(RepoUrl + "/branches/mcp-tmp-", StringComparison.Ordinal))
            {
                StagingBranch = null;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (method == "POST" && path == RepoUrl + "/pulls")
                return Json(HttpStatusCode.Created, """{"number":42,"title":"Update room settings","state":"open","html_url":"https://gitea.test/pulls/42","head":{"ref":"feature/room-yaml"},"base":{"ref":"main"}}""");

            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed record CapturedRequest(string Method, string Path, string? Body, string? Authorization);
}
