using System.Diagnostics;
using System.Net;

namespace Shortener.Core;

public sealed class Engine(JsonStore store, IAgent agent, int parallelism = 3, int timeoutSeconds = 60, IAgent? fallback = null)
{
    private readonly SemaphoreSlim tickLock = new(1, 1);
    public void Recover() => store.Change(db =>
    {
        foreach (var r in db.Runs.Values)
        foreach (var n in r.Nodes.Where(n => n.Status == NodeStatus.Running))
        {
            n.RecoveryStartedAt = DateTimeOffset.UtcNow;
            n.Status = n.Attempts >= n.Spec.MaxAttempts ? NodeStatus.Failed : NodeStatus.Pending;
            n.Error = "Process interrupted; prior external call outcome unknown";
            if (n.Status == NodeStatus.Failed && r.Status == RunStatus.Active) r.Status = RunStatus.RollingBack;
            db.Log(r.Id, "engine", "recovered", n.Spec.Id);
        }
        return true;
    });
    public async Task TickAsync(CancellationToken cancellation = default)
    {
        await tickLock.WaitAsync(cancellation);
        try
        {
            var jobs = store.Change(db =>
            {
                var result = new List<AgentRequest>();
                foreach (var r in db.Runs.Values)
                {
                    foreach (var revision in r.CleanupRevisions) agent.InvalidateWorkspace(r.Id, revision);
                    r.CleanupRevisions.Clear();
                    if (r.Status == RunStatus.Stopping && r.Nodes.All(n => n.Status != NodeStatus.Running))
                    {
                        r.Status = RunStatus.Stopped; db.Log(r.Id, "engine", "stopped", "No in-flight work");
                    }
                    if (r.Status == RunStatus.RollingBack)
                    {
                        agent.InvalidateWorkspace(r.Id, r.Revision);
                        while (r.Nodes.Any(n => n.Status == NodeStatus.Succeeded))
                        {
                            var leaf = r.Nodes.First(n => n.Status == NodeStatus.Succeeded &&
                                !r.Nodes.Any(other => other.Status == NodeStatus.Succeeded && other.Spec.DependsOn.Contains(n.Spec.Id)));
                            leaf.Status = NodeStatus.Compensated;
                            db.Log(r.Id, "engine", "compensated", leaf.Spec.Id + ": artifact invalidated; retained for audit");
                        }
                        r.Status = RunStatus.RolledBack; db.Log(r.Id, "engine", "rolled-back", "Artifact compensation complete");
                    }
                    if (r.Status != RunStatus.Active) continue;
                    // Approval survives restart as a concrete identity, never just a boolean.
                    // Preflight the run before claiming any new work or consuming an attempt.
                    var mismatched = r.Nodes.FirstOrDefault(n => n.Status == NodeStatus.Pending && n.UseFallback &&
                        n.ApprovedFallback?.Matches(fallback) != true);
                    if (mismatched is not null)
                    {
                        mismatched.Status = NodeStatus.Failed;
                        mismatched.Error = nameof(FallbackIdentityMismatchException);
                        r.Status = RunStatus.RollingBack;
                        LogFallbackMismatch(db, r, mismatched);
                        continue;
                    }
                    if (r.Nodes.All(n => n.Status == NodeStatus.Succeeded))
                    {
                        r.Status = RunStatus.Completed; db.Log(r.Id, "engine", "completed", "All nodes succeeded"); continue;
                    }
                    foreach (var n in r.Nodes.Where(n => n.Status == NodeStatus.Pending && n.RetryAt <= DateTimeOffset.UtcNow &&
                        (n.UseFallback ? n.FallbackAttempts < 1 : n.Attempts < n.Spec.MaxAttempts) &&
                        WorkflowService.Ready(r, n) && (!n.Spec.Approval || n.Approved)))
                    {
                        if (result.Count >= Math.Clamp(parallelism, 1, 16)) break;
                        n.Status = NodeStatus.Running;
                        if (n.UseFallback) n.FallbackAttempts++; else n.Attempts++;
                        db.Log(r.Id, "engine", "attempt-started", $"{n.Spec.Id}; attempt={n.Attempts}; revision={r.Revision}");
                        result.Add(new(r.Id, r.Revision, r.Context, n.Spec,
                            n.Spec.DependsOn.ToDictionary(id => id, id => r.Nodes.Single(x => x.Spec.Id == id).Output!),
                            $"{r.Id}:{r.Revision}:{n.Spec.Id}:{(n.UseFallback ? "fallback" : "primary")}", r.Engineering,
                            r.Contract, r.ValidateBaseline, n.UseFallback, n.ApprovedFallback));
                    }
                }
                return result;
            });
            await Task.WhenAll(jobs.Select(job => Execute(job, cancellation)));
        }
        finally { tickLock.Release(); }
    }
    private async Task Execute(AgentRequest job, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        string? output = null;
        Exception? error = null;
        SecurityReport? security = null;
        var executor = job.UseFallback ? fallback ?? agent : agent;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds((job.Engineering is not null || job.ValidateBaseline) && job.Node.Id == "tests" ? 300 : Math.Clamp(timeoutSeconds, 1, 300)));
        try
        {
            // Defense in depth if a custom adapter changes identity after the claim.
            if (job.UseFallback && job.ApprovedFallback?.Matches(fallback) != true)
                throw new FallbackIdentityMismatchException();
            output = executor is EngineeringAgent
                ? await executor.ExecuteAsync(job, timeout.Token)
                : await executor.ExecuteAsync(job, timeout.Token).WaitAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(output) || output.Length > 100_000) throw new InvalidDataException("Invalid artifact size");
            if (job.Node.Id == "security") security = Quality.Parse(output, job);
        }
        catch (Exception ex) { error = ex; }
        store.Change(db =>
        {
            var r = db.Runs[job.RunId];
            var n = r.Nodes.Single(n => n.Spec.Id == job.Node.Id);
            n.DurationMs += watch.ElapsedMilliseconds;
            if (r.Status != RunStatus.Active || cancellation.IsCancellationRequested)
            {
                n.Status = NodeStatus.Pending;
                // Consumed attempt stays consumed, even when the result is discarded.
                if (n.Attempts >= n.Spec.MaxAttempts) n.Status = NodeStatus.Failed;
                if (r.Status == RunStatus.Active && n.Status == NodeStatus.Failed) r.Status = RunStatus.RollingBack;
                if (r.Status == RunStatus.Paused && r.FallbackProposal is not null && n.Status == NodeStatus.Failed)
                { r.Status = RunStatus.RollingBack; r.FallbackProposal = null; }
                db.Log(r.Id, "engine", "result-discarded", n.Spec.Id);
            }
            else if (error is null)
            {
                n.Status = NodeStatus.Succeeded; n.Output = output; n.Error = null;
                var artifact = RecordArtifact(db, r, n, job, output!, security is null || !Quality.Blocks(security), executor);
                if (security is not null && Quality.Blocks(security))
                {
                    try
                    {
                        r.FindingsProposal = Quality.Propose(r, artifact, security); r.Status = RunStatus.Paused;
                        db.Log(r.Id, "engine", "security-blocked-replan-proposed", System.Text.Json.JsonSerializer.Serialize(r.FindingsProposal));
                    }
                    catch (InvalidDataException)
                    { r.Status = RunStatus.RollingBack; db.Log(r.Id, "engine", "security-blocked", "No valid remediation proposal or revision budget"); }
                }
                if (n.IncidentStartedAt is { } incident)
                {
                    db.Log(r.Id, "engine", "incident-recovered", System.Text.Json.JsonSerializer.Serialize(new {
                        nodeId = n.Spec.Id, recoveryMs = (DateTimeOffset.UtcNow - incident).TotalMilliseconds, fallback = n.UseFallback }));
                    n.IncidentStartedAt = null;
                }
                if (n.RecoveryStartedAt is { } recovered)
                {
                    db.Log(r.Id, "engine", "recovery-completed", System.Text.Json.JsonSerializer.Serialize(new {
                        nodeId = n.Spec.Id, recoveryMs = (DateTimeOffset.UtcNow - recovered).TotalMilliseconds }));
                    n.RecoveryStartedAt = null;
                }
            }
            else
            {
                if (error is FallbackIdentityMismatchException) LogFallbackMismatch(db, r, n);
                if (error is EngineeringValidationException validation)
                {
                    n.Output = validation.Report;
                    RecordArtifact(db, r, n, job, validation.Report, false, executor);
                }
                if (job.Node.Id == "security" && output is { Length: <= 100000 })
                { n.Output = output; RecordArtifact(db, r, n, job, output, false, executor); }
                var retryable = error is OperationCanceledException || error is HttpRequestException h &&
                    (h.StatusCode is null or HttpStatusCode.TooManyRequests || (int)h.StatusCode >= 500);
                n.Error = error is HttpRequestException http ? $"AI HTTP status: {http.StatusCode}" : error.GetType().Name;
                n.IncidentStartedAt ??= DateTimeOffset.UtcNow;
                if (retryable && !n.UseFallback && n.Attempts < n.Spec.MaxAttempts)
                {
                    n.Status = NodeStatus.Pending;
                    n.RetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, n.Attempts - 1));
                    db.Log(r.Id, "engine", "retry-scheduled", $"{n.Spec.Id}; {n.Error}; at={n.RetryAt:O}");
                }
                else if (retryable && !n.UseFallback && fallback is not null)
                {
                    n.Status = NodeStatus.Pending; r.Status = RunStatus.Paused;
                    r.FallbackProposal = new(r.Revision, n.Spec.Id, fallback.Identity, fallback.Model, "Primary retry budget exhausted; one fallback attempt requires approval");
                    db.Log(r.Id, "engine", "fallback-proposed", System.Text.Json.JsonSerializer.Serialize(r.FallbackProposal));
                }
                else
                {
                    n.Status = NodeStatus.Failed; r.Status = RunStatus.RollingBack;
                    db.Log(r.Id, "engine", "node-failed", $"{n.Spec.Id}; {n.Error}");
                }
            }
            return true;
        });
    }
    private ArtifactRecord RecordArtifact(Database db, Workflow run, WorkNode node, AgentRequest job, string output, bool success, IAgent executor)
    {
        var artifact = new ArtifactRecord(Guid.NewGuid().ToString("N"), run.Revision, Trace.RequirementHash(run),
            node.Spec.Id, executor.Identity, executor.Model, job.Dependencies.ToDictionary(p => p.Key, p => Trace.Hash(p.Value)),
            Trace.Hash(output), output, DateTimeOffset.UtcNow, success);
        run.Artifacts.Add(artifact); node.ArtifactId = artifact.Id;
        db.Log(run.Id, "engine", success ? "node-succeeded" : "validation-failed",
            System.Text.Json.JsonSerializer.Serialize(new { nodeId = node.Spec.Id, artifact.Id, artifact.Revision,
                artifact.RequirementHash, artifact.Agent, artifact.Model, artifact.InputHashes, artifact.Sha256, success }));
        return artifact;
    }
    private void LogFallbackMismatch(Database db, Workflow run, WorkNode node) =>
        db.Log(run.Id, "engine", "fallback-identity-mismatch", System.Text.Json.JsonSerializer.Serialize(new {
            revision = run.Revision, nodeId = node.Spec.Id, approved = node.ApprovedFallback,
            configured = fallback is null ? null : new FallbackIdentity(fallback.Identity, fallback.Model),
            action = "No fallback call authorized; rollback required" }));
}

sealed class FallbackIdentityMismatchException : Exception;
