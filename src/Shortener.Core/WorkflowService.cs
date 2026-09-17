using System.Text.Json;

namespace Shortener.Core;

public sealed class WorkflowService(JsonStore store)
{
    public Workflow Create(string scenario, string context, string actor, EngineeringRequirement? engineering = null, bool validateBaseline = false)
    {
        if (string.IsNullOrWhiteSpace(context) || context.Length > 16000) throw new ArgumentException("Context must contain 1–16000 characters");
        var specs = Scenarios.Create(scenario);
        if (engineering is not null || validateBaseline)
        {
            if (engineering is not null) PatchPolicy.ValidateRequirement(engineering);
            specs = specs.Select(n => n.Id == "tests" ? n with { Approval = true,
                Task = "Review the implementation patch, then build and run baseline, red regression and green regression in the constrained workspace." } : n).ToArray();
        }
        Scenarios.Validate(specs);
        return store.Change(db =>
        {
            var run = new Workflow { Scenario = scenario, Context = context, Engineering = engineering,
                Contract = Quality.Contract(scenario), ValidateBaseline = validateBaseline,
                Nodes = specs.Select(s => new WorkNode { Spec = s }).ToList() };
            db.Runs.Add(run.Id, run);
            db.Log(run.Id, actor, "created", JsonSerializer.Serialize(new { scenario, context, engineering,
                requirementHash = Trace.RequirementHash(run), nodes = specs, revision = 1 }));
            return run;
        });
    }
    public Workflow Get(string id) => store.Read(db => db.Runs[id]);
    public static bool Ready(Workflow run, WorkNode node) => node.Spec.DependsOn.All(id =>
        run.Nodes.Single(n => n.Spec.Id == id).Status == NodeStatus.Succeeded);
    public Workflow Approve(string id, string nodeId, int revision, bool accept, string reason, string actor)
    {
        RequireReason(reason);
        return store.Change(db =>
        {
            var r = db.Runs[id];
            var n = r.Nodes.SingleOrDefault(n => n.Spec.Id == nodeId) ?? throw new KeyNotFoundException();
            if (r.Revision != revision || r.Status != RunStatus.Active || n.Status != NodeStatus.Pending ||
                !n.Spec.Approval || n.Approved || !Ready(r, n)) throw new Conflict("Gate is not ready or revision is stale");
            if (accept && nodeId == "requirements" && r.Contract?.Decisions.Any(p => p.Value is null) == true)
                throw new Conflict("Resolve the outstanding scope questions before approving requirements");
            if (accept) n.Approved = true;
            else r.Status = RunStatus.RollingBack;
            db.Log(id, actor, accept ? "gate-approved" : "gate-rejected", JsonSerializer.Serialize(new {
                revision, nodeId, reason, requirementHash = Trace.RequirementHash(r),
                artifactHashes = n.Spec.DependsOn.ToDictionary(dep => dep, dep =>
                    r.Nodes.Single(x => x.Spec.Id == dep).Output is { } output ? Trace.Hash(output) : "") }));
            return r;
        });
    }
    public Workflow Stop(string id, string actor) => store.Change(db =>
    {
        var r = db.Runs[id];
        if (r.Status is not (RunStatus.Active or RunStatus.Paused)) throw new Conflict("Run cannot be stopped in this state");
        r.Status = RunStatus.Stopping;
        r.Proposal = null;
        r.RequirementProposal = null;
        r.FindingsProposal = null; r.FallbackProposal = null;
        db.Log(id, actor, "stop-requested", "Dispatch disabled; in-flight results will be discarded");
        return r;
    });
    public Workflow Rollback(string id, string actor) => store.Change(db =>
    {
        var r = db.Runs[id];
        if (r.Status is not (RunStatus.Completed or RunStatus.Stopped)) throw new Conflict("Stop the run before rollback");
        r.Status = RunStatus.RollingBack;
        db.Log(id, actor, "rollback-requested", "Invalidate committed artifacts in reverse dependency order");
        return r;
    });
    public Workflow Propose(string id, NodeSpec[] nodes, string reason, int revision, string actor)
    {
        RequireReason(reason);
        Scenarios.Validate(nodes);
        return store.Change(db =>
        {
            var r = db.Runs[id];
            if (r.Status != RunStatus.Active || r.Revision != revision || r.Revision >= 4 ||
                r.Nodes.Any(n => n.Status == NodeStatus.Running)) throw new Conflict("Replan requires a quiescent active run, current revision and remaining revision budget");
            if (r.Engineering is not null) throw new Conflict("Engineering runs use requirement-change proposals so workspace and evidence revisions remain aligned");
            foreach (var old in r.Nodes)
            {
                var replacement = nodes.SingleOrDefault(n => n.Id == old.Spec.Id);
                if ((old.Attempts > 0 || old.Approved || old.Spec.Approval) &&
                    JsonSerializer.Serialize(old.Spec) != JsonSerializer.Serialize(replacement))
                    throw new Conflict("Cannot alter started, approved or mandatory gate nodes");
            }
            r.Proposal = new(reason, revision, nodes);
            r.Status = RunStatus.Paused;
            db.Log(id, actor, "replan-proposed", JsonSerializer.Serialize(r.Proposal));
            return r;
        });
    }
    public Workflow DecidePlan(string id, int revision, bool accept, string reason, string actor)
    {
        RequireReason(reason);
        return store.Change(db =>
        {
            var r = db.Runs[id];
            if (r.Status != RunStatus.Paused || r.Proposal is null || r.Revision != revision)
                throw new Conflict("No matching pending plan");
            if (accept)
            {
                r.Nodes = r.Proposal.Nodes.Select(s => r.Nodes.SingleOrDefault(n => n.Spec.Id == s.Id) is { } old
                    ? new WorkNode { Spec = s, Status = old.Status, Attempts = old.Attempts, Approved = old.Approved,
                        Output = old.Output, Error = old.Error, RetryAt = old.RetryAt, DurationMs = old.DurationMs,
                        ArtifactId = old.ArtifactId, RecoveryStartedAt = old.RecoveryStartedAt,
                        IncidentStartedAt = old.IncidentStartedAt, UseFallback = old.UseFallback, FallbackAttempts = old.FallbackAttempts,
                        ApprovedFallback = old.ApprovedFallback }
                    : new WorkNode { Spec = s }).ToList();
                r.Revision++;
            }
            r.Proposal = null; r.Status = RunStatus.Active;
            db.Log(id, actor, accept ? "replan-approved" : "replan-rejected", $"revision={r.Revision}; {reason}");
            return r;
        });
    }
    public Workflow ProposeRequirement(string id, string context, EngineeringRequirement? engineering,
        string reason, int revision, string actor)
    {
        RequireReason(reason);
        if (string.IsNullOrWhiteSpace(context) || context.Length > 16000) throw new ArgumentException("Invalid context");
        if (engineering is not null) PatchPolicy.ValidateRequirement(engineering);
        return store.Change(db =>
        {
            var r = db.Runs[id];
            if (r.Status is not (RunStatus.Active or RunStatus.Completed) || r.Revision != revision || r.Revision >= 4 ||
                r.Nodes.Any(n => n.Status == NodeStatus.Running)) throw new Conflict("Requirement change needs a quiescent active/completed run and current revision budget");
            if ((r.Engineering is null) != (engineering is null)) throw new Conflict("Cannot switch engineering execution mode during a run");
            if (r.Context == context && Trace.HashJson(r.Engineering) == Trace.HashJson(engineering)) throw new Conflict("Requirement has not changed");
            r.RequirementProposal = new(context, engineering, reason, revision, r.Status);
            r.Status = RunStatus.Paused;
            db.Log(id, actor, "requirement-change-proposed", JsonSerializer.Serialize(r.RequirementProposal));
            return r;
        });
    }
    public Workflow DecideRequirement(string id, int revision, bool accept, string reason, string actor)
    {
        RequireReason(reason);
        return store.Change(db =>
        {
            var r = db.Runs[id];
            if (r.Status != RunStatus.Paused || r.RequirementProposal is not { } proposal || r.Revision != revision)
                throw new Conflict("No matching requirement proposal");
            if (accept)
            {
                r.History.Add(new(r.Revision, r.Context, r.Engineering, r.Nodes, proposal.PreviousStatus));
                r.CleanupRevisions.Add(r.Revision);
                var invalidated = r.Nodes.Select(n => new { n.Spec.Id, n.ArtifactId, n.Approved, n.Attempts }).ToArray();
                r.Context = proposal.Context; r.Engineering = proposal.Engineering; r.Revision++;
                // A root requirement changed: invalidate the entire transitive closure,
                // including release and execution approvals. Original evidence remains in History/Artifacts.
                r.Nodes = r.Nodes.Select(n => new WorkNode { Spec = n.Spec }).ToList();
                db.Log(id, actor, "requirement-change-approved", JsonSerializer.Serialize(new {
                    revision = r.Revision, reason, requirementHash = Trace.RequirementHash(r), invalidated }));
            }
            else db.Log(id, actor, "requirement-change-rejected", JsonSerializer.Serialize(new { revision, reason }));
            r.RequirementProposal = null;
            r.Status = r.Nodes.All(n => n.Status == NodeStatus.Succeeded) ? RunStatus.Completed : RunStatus.Active;
            return r;
        });
    }
    private static void RequireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 4000) throw new ArgumentException("A reason of 1–4000 characters is required");
    }
    public Workflow Clarify(string id, int revision, Dictionary<string, string> answers, string actor) => store.Change(db =>
    {
        var r = db.Runs[id];
        if (r.Scenario != "ambiguous" || r.Status != RunStatus.Active || r.Revision != revision ||
            r.Nodes.Any(n => n.Attempts > 0 || n.Approved) || r.Contract is null) throw new Conflict("Clarifications require an unstarted, unapproved ambiguous run");
        if (answers is null || answers.Count != Quality.SupportedDecisions.Count ||
            Quality.SupportedDecisions.Any(p => !answers.TryGetValue(p.Key, out var answer) || answer != p.Value))
            throw new ArgumentException("Supported answers: creation=operator-only, retention=optional-expiry, aliases=case-sensitive; other scope needs a new implementation capability");
        foreach (var p in answers) r.Contract.Decisions[p.Key] = p.Value;
        db.Log(id, actor, "scope-clarified", JsonSerializer.Serialize(new { revision, answers, requirementHash = Trace.RequirementHash(r) }));
        return r;
    });
    public Workflow DecideFindings(string id, int revision, bool accept, string reason, string actor)
    {
        RequireReason(reason);
        return store.Change(db =>
        {
            var r = db.Runs[id];
            if (r.Status != RunStatus.Paused || r.FindingsProposal is not { } p || revision != r.Revision ||
                r.Nodes.Any(n => n.Status == NodeStatus.Running) || r.Revision >= 4 ||
                !r.Artifacts.Any(a => a.Id == p.SourceArtifactId && a.Sha256 == p.SourceHash)) throw new Conflict("No current, quiescent findings proposal");
            if (accept)
            {
                Scenarios.Validate(p.Nodes);
                r.History.Add(new(r.Revision, r.Context, r.Engineering, r.Nodes, RunStatus.Paused));
                r.CleanupRevisions.Add(r.Revision); r.Engineering = p.Engineering;
                r.Context += "\nApproved security remediation: " + string.Join(", ", p.FindingIds);
                r.Revision++; r.Nodes = p.Nodes.Select(spec => new WorkNode { Spec = spec }).ToList();
                r.Status = RunStatus.Active;
            }
            else r.Status = RunStatus.RollingBack; // No waiver of unresolved security blockers.
            db.Log(id, actor, accept ? "findings-plan-approved" : "findings-plan-rejected", JsonSerializer.Serialize(new {
                revision = r.Revision, reason, p.SourceArtifactId, p.SourceHash, p.FindingIds, requirementHash = Trace.RequirementHash(r) }));
            r.FindingsProposal = null; return r;
        });
    }
    public Workflow DecideFallback(string id, int revision, bool accept, string reason, string actor)
    {
        RequireReason(reason);
        return store.Change(db =>
        {
            var r = db.Runs[id];
            if (r.Status != RunStatus.Paused || r.FallbackProposal is not { } p || revision != r.Revision ||
                r.Nodes.Any(n => n.Status == NodeStatus.Running)) throw new Conflict("No current quiescent fallback proposal");
            if (accept)
            {
                var node = r.Nodes.Single(n => n.Spec.Id == p.NodeId);
                node.ApprovedFallback = new(p.Agent, p.Model);
                node.UseFallback = true; r.Status = RunStatus.Active;
            }
            else r.Status = RunStatus.RollingBack;
            db.Log(id, actor, accept ? "fallback-approved" : "fallback-rejected", JsonSerializer.Serialize(new { revision, p.NodeId, p.Agent, p.Model, reason }));
            r.FallbackProposal = null; return r;
        });
    }
}
