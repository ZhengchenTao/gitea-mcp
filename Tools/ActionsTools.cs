using GiteaMcp.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace GiteaMcp.Tools;

/// <summary>Gitea Actions Tool：list_workflow_runs / read_run_log</summary>
[McpServerToolType]
public class ActionsTools(
    GiteaApiClient gitea,
    GiteaRepoFilter filter)
{
    [McpServerTool]
    [Description(
        "List recent Gitea Actions workflow runs for a repository. " +
        "Filter by branch name or run status (queued/in_progress/success/failure/cancelled/skipped). " +
        "Returns: run ID, workflow name, triggering event, branch, status, conclusion, actor, and timestamps. " +
        "Use read_run_log to get the full log output of a specific run or job.")]
    public async Task<object> list_workflow_runs(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Filter by branch name. Optional.")] string? branch = null,
        [Description("Filter by status: 'queued', 'in_progress', 'success', 'failure', 'cancelled', 'skipped'. Optional.")] string? status = null,
        [Description("Max runs to return. Default 30.")] int? limit = null,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        var lim = Math.Min(limit ?? 30, 100);
        var result = await gitea.GetWorkflowRunsAsync(owner, repo, branch, status, lim, ct);

        return new
        {
            total = result.TotalCount,
            runs = result.WorkflowRuns.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                @event = r.Event,
                branch = r.HeadBranch,
                sha = r.HeadSha,
                status = r.Status,
                conclusion = r.Conclusion,
                actor = r.Actor?.Login,
                html_url = r.HtmlUrl,
                created_at = r.CreatedAt,
                updated_at = r.UpdatedAt,
            }).ToList(),
        };
    }

    [McpServerTool]
    [Description(
        "Get detailed info and log output for a specific Gitea Actions workflow run. " +
        "Returns: run overview (status, conclusion, timing) + all jobs with their status. " +
        "Log output is truncated to 1MB; long logs will have '[...log truncated...]' appended. " +
        "When job_id is provided, fetches that specific job's log; otherwise fetches the run-level log. " +
        "Use list_workflow_runs to find the run_id.")]
    public async Task<object> read_run_log(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Workflow run ID (from list_workflow_runs).")] long run_id,
        [Description("Specific job ID to fetch logs for. Omit to get the run-level log.")] long? job_id = null,
        CancellationToken ct = default)
    {
        if (filter.IsBlocked($"{owner}/{repo}"))
            throw new UnauthorizedAccessException($"Repo {owner}/{repo} is on the access blocklist.");

        var runTask = gitea.GetWorkflowRunAsync(owner, repo, run_id, ct);
        var jobsTask = gitea.GetRunJobsAsync(owner, repo, run_id, ct);
        var logTask = gitea.GetRunLogAsync(owner, repo, run_id, job_id, ct: ct);

        await Task.WhenAll(runTask, jobsTask, logTask);

        var run = await runTask;
        var jobList = await jobsTask;
        var log = await logTask;

        return new
        {
            run = new
            {
                id = run.Id,
                name = run.Name,
                @event = run.Event,
                branch = run.HeadBranch,
                sha = run.HeadSha,
                status = run.Status,
                conclusion = run.Conclusion,
                actor = run.Actor?.Login,
                html_url = run.HtmlUrl,
                created_at = run.CreatedAt,
                updated_at = run.UpdatedAt,
            },
            jobs = jobList.WorkflowJobs.Select(j => new
            {
                id = j.Id,
                name = j.Name,
                status = j.Status,
                conclusion = j.Conclusion,
                started_at = j.StartedAt,
                completed_at = j.CompletedAt,
            }).ToList(),
            log,
        };
    }
}
