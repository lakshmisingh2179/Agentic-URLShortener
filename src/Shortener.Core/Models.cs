namespace Shortener.Core;

public enum NodeStatus { Pending, Running, Succeeded, Failed, Compensated }
public enum RunStatus { Active, Paused, Stopping, Stopped, RollingBack, RolledBack, Completed }
public sealed record NodeSpec(string Id, string Role, string Task, string[] DependsOn,
    bool Approval = false, int MaxAttempts = 3);
public sealed class WorkNode
{
    public required NodeSpec Spec { get; set; }
    public NodeStatus Status { get; set; }
    public int Attempts { get; set; }
    public bool Approved { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset RetryAt { get; set; }
    public long DurationMs { get; set; }
    public string? ArtifactId { get; set; }
    public DateTimeOffset? RecoveryStartedAt { get; set; }
    public DateTimeOffset? IncidentStartedAt { get; set; }
    public bool UseFallback { get; set; }
    public int FallbackAttempts { get; set; }
    public FallbackIdentity? ApprovedFallback { get; set; }
}
public sealed record PlanProposal(string Reason, int BaseRevision, NodeSpec[] Nodes);
public sealed record EngineeringRequirement(string Id, string[] ReservedAliases);
public sealed record RequirementProposal(string Context, EngineeringRequirement? Engineering, string Reason, int BaseRevision,
    RunStatus PreviousStatus = RunStatus.Active);
public sealed record ArtifactRecord(string Id, int Revision, string RequirementHash, string NodeId,
    string Agent, string Model, Dictionary<string, string> InputHashes, string Sha256, string Content,
    DateTimeOffset At, bool Success);
public sealed record RevisionSnapshot(int Revision, string Context, EngineeringRequirement? Engineering,
    List<WorkNode> Nodes, RunStatus Status);
public sealed record ScopeContract(string Scenario, Dictionary<string, string> AcceptanceCriteria,
    Dictionary<string, string?> Decisions);
public sealed record SecurityFinding(string Id, string Severity, string Status, string Evidence, string Remediation);
public sealed record SecurityReport(string RequirementHash, string Verdict, SecurityFinding[] Findings,
    EngineeringRequirement? RecommendedEngineering = null);
public sealed record FindingsProposal(int BaseRevision, string SourceArtifactId, string SourceHash,
    string[] FindingIds, NodeSpec[] Nodes, EngineeringRequirement? Engineering, string Reason);
public sealed record FallbackProposal(int Revision, string NodeId, string Agent, string Model, string Reason);
public sealed record FallbackIdentity(string Agent, string Model)
{
    public bool Matches(IAgent? configured) => configured is not null &&
        !string.IsNullOrWhiteSpace(Agent) && !string.IsNullOrWhiteSpace(Model) &&
        string.Equals(Agent, configured.Identity, StringComparison.Ordinal) &&
        string.Equals(Model, configured.Model, StringComparison.Ordinal);
}
public sealed class Workflow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Scenario { get; set; }
    public required string Context { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Active;
    public int Revision { get; set; } = 1;
    public List<WorkNode> Nodes { get; set; } = [];
    public PlanProposal? Proposal { get; set; }
    public EngineeringRequirement? Engineering { get; set; }
    public RequirementProposal? RequirementProposal { get; set; }
    public List<ArtifactRecord> Artifacts { get; set; } = [];
    public List<RevisionSnapshot> History { get; set; } = [];
    public List<int> CleanupRevisions { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ScopeContract? Contract { get; set; }
    public bool ValidateBaseline { get; set; }
    public FindingsProposal? FindingsProposal { get; set; }
    public FallbackProposal? FallbackProposal { get; set; }
}
public sealed record AuditEvent(long Sequence, DateTimeOffset At, string RunId,
    string Actor, string Action, string Detail, string PreviousHash = "", string Hash = "");
public sealed class Link
{
    public required string Code { get; set; }
    public required string Url { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public long Clicks { get; set; }
}
public sealed class Database
{
    public Dictionary<string, Workflow> Runs { get; set; } = [];
    public Dictionary<string, Link> Links { get; set; } = [];
    public List<AuditEvent> Audit { get; set; } = [];
    public void Log(string run, string actor, string action, string detail)
    {
        var entry = new AuditEvent(Audit.Count + 1L, DateTimeOffset.UtcNow, run, actor, action, detail,
            Audit.LastOrDefault()?.Hash ?? "");
        Audit.Add(entry with { Hash = Trace.HashJson(entry) });
    }
}
public sealed class Conflict(string message) : Exception(message);
