using System.Net;
using System.Text.Json;
using Shortener.Core;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: EngineeringDemo <baseline-directory> <workspace-directory> <evidence-directory> [--auto-approve-offline]");
    return 2;
}
var baseline = Path.GetFullPath(args[0]); var workspaceRoot = Path.GetFullPath(args[1]); var evidence = Path.GetFullPath(args[2]);
var auto = args.Contains("--auto-approve-offline");
var live = Environment.GetEnvironmentVariable("AGENT_MODE") == "live";
if (auto && live) throw new InvalidOperationException("Automatic approvals are restricted to the explicit offline demonstration");
var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? throw new InvalidOperationException("Set DOTNET_HOST_PATH to an absolute SDK dotnet executable");
Directory.CreateDirectory(evidence);
using var store = new JsonStore(Path.Combine(workspaceRoot, "store"));
var service = new WorkflowService(store);
IAgent inner = live ? new LiveAgent(new HttpClient(),
    Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("API key required"),
    Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? throw new InvalidOperationException("Model required")) : new DemoAgent();
var agent = new EngineeringAgent(inner, baseline, Path.Combine(workspaceRoot, "candidates"), dotnet);
var engine = new Engine(store, agent);
var run = service.Create("brownfield", "REQ-ALIAS-001: Reserve admin case-insensitively for new aliases. Existing stored aliases must continue to resolve.",
    "demo-operator", new("REQ-ALIAS-001", ["admin"]));
Console.WriteLine($"Engineering run {run.Id}; baseline {baseline}");
await Complete(run.Id, engine);
ExportRevision(run.Id, 1);
service.ProposeRequirement(run.Id, "REQ-ALIAS-001 changed: reserve both admin and health case-insensitively for new aliases; preserve existing records.",
    new("REQ-ALIAS-001", ["admin", "health"]), "Upstream scope expanded after revision 1 validated and was approved", 1, "demo-operator");
Save("requirement-proposal.json", service.Get(run.Id));
if (!Decide("Review requirement change: admin -> admin + health; all downstream artifacts and approvals will be invalidated."))
    throw new InvalidOperationException("Demo requires approval to proceed with changed requirements");
service.DecideRequirement(run.Id, 1, true, auto ? "Explicit offline simulated approval" : "Human approved changed requirement", "demo-approver");
var invalidated = service.Get(run.Id);
if (invalidated.Revision != 2 || invalidated.Nodes.Any(n => n.Approved || n.Output is not null || n.Status != NodeStatus.Pending))
    throw new InvalidOperationException("Invalidation invariant failed");
Save("invalidation.json", invalidated);
await Complete(run.Id, engine);
ExportRevision(run.Id, 2);
Save("superseded-workspace.json", new {
    sourceRestored = Trace.Hash(File.ReadAllText(Path.Combine(agent.Workspace(run.Id, 1), PatchPolicy.SourcePath))) ==
        Trace.Hash(EngineeringAgent.ReadBaseline(baseline)[PatchPolicy.SourcePath]),
    invalidatedMarker = File.Exists(Path.Combine(agent.Workspace(run.Id, 1), "INVALIDATED.json")) });

foreach (var scenario in new[] { "greenfield", "ambiguous" })
{
    var scoped = service.Create(scenario, scenario == "greenfield"
        ? "Deliver the included from-scratch C# API: operator-created links, optional expiry, unique aliases, 302 redirects and persistent counts; validate every acceptance criterion."
        : "Make links short and track usage. Creation policy, expiry and alias semantics must be resolved before any agent executes.",
        "demo-operator", validateBaseline: true);
    if (scenario == "ambiguous")
    {
        Save("scenarios/ambiguous/unresolved.json", scoped);
        var blocked = false;
        try { service.Approve(scoped.Id, "requirements", 1, true, "Try without answers", "demo-approver"); }
        catch (Conflict) { blocked = true; }
        if (!blocked) throw new InvalidOperationException("Unresolved scope was not blocked");
        if (!Decide("Resolve ambiguous scope as operator-only creation, optional-expiry retention and case-sensitive aliases?"))
            throw new InvalidOperationException("Scope unresolved");
        service.Clarify(scoped.Id, 1, Quality.SupportedDecisions, "demo-operator");
        Save("scenarios/ambiguous/resolved.json", service.Get(scoped.Id));
    }
    await Complete(scoped.Id, engine);
    Save($"scenarios/{scenario}/workflow.json", service.Get(scoped.Id));
    foreach (var file in new[] { "validation.json", "baseline-manifest.json", PatchPolicy.SourcePath })
    {
        var target = Path.Combine(evidence, "scenarios", scenario, file); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(agent.Workspace(scoped.Id, 1), file), target, true);
    }
}

// Deterministic fault injection, explicitly identified as such in the audit.
// These exercise the production recovery/retry/compensation state transitions.
if (!live)
{
    var findingRun = service.Create("brownfield", "Security exercise: reserve admin; a review may discover a missing protected operational alias.",
        "demo-operator", new("REQ-ALIAS-SEC", ["admin"]));
    var findingsAgent = new EngineeringAgent(new FindingDrivenAgent(), baseline, Path.Combine(workspaceRoot, "candidates"), dotnet);
    await Complete(findingRun.Id, new Engine(store, findingsAgent));
    Save("findings/workflow.json", service.Get(findingRun.Id));
    foreach (var file in new[] { "validation.json", "patch.json", "change.diff" })
    {
        var target = Path.Combine(evidence, "findings", file); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(findingsAgent.Workspace(findingRun.Id, 2), file), target, true);
    }
    var fallbackRun = service.Create("greenfield", "Explicit fallback exercise: primary requirements provider unavailable; require approval before the alternate executes.", "demo-operator");
    await Complete(fallbackRun.Id, new Engine(store, new PrimaryOutageAgent(), fallback: new DemoAgent()));
    Save("fallback/workflow.json", service.Get(fallbackRun.Id));
    var recoveredRun = service.Create("greenfield", "Injected restart and one transient failure for reliability measurement", "demo-operator");
    store.Change(db =>
    {
        var node = db.Runs[recoveredRun.Id].Nodes[0]; node.Status = NodeStatus.Running; node.Attempts = 1;
        db.Log(recoveredRun.Id, "demo-fault-injector", "attempt-started", "Synthetic interrupted attempt for deterministic recovery exercise");
        db.Log(recoveredRun.Id, "demo-fault-injector", "crash-injected", "No OS process was killed; persisted Running state is injected"); return true;
    });
    var recoveryEngine = new Engine(store, new FailOnceAgent()); recoveryEngine.Recover();
    await Complete(recoveredRun.Id, recoveryEngine);
    var failedRun = service.Create("greenfield", "Injected permanent failure for rollback measurement", "demo-operator");
    var failureEngine = new Engine(store, new PermanentFailureAgent());
    await failureEngine.TickAsync(); await failureEngine.TickAsync();
    if (service.Get(failedRun.Id).Status != RunStatus.RolledBack) throw new InvalidOperationException("Rollback did not finish");
}
Save("audit.json", store.Read(d => d.Audit));
Save("reliability.json", store.Read(Reliability.Calculate));
Save("traceability.json", service.Get(run.Id).Artifacts);
Save("manifest.json", new {
    runId = run.Id, agent = agent.Identity, agent.Model, liveCalls = live, simulatedApprovals = auto,
    faultInjection = !live, auditValid = store.Read(d => Trace.VerifyAudit(d.Audit)),
    auditHead = store.Read(d => d.Audit.Last().Hash),
    files = Directory.EnumerateFiles(evidence, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != "manifest.json")
        .Order().ToDictionary(p => Path.GetRelativePath(evidence, p).Replace('\\', '/'), p => Trace.Hash(File.ReadAllText(p))) });
Console.WriteLine("Engineering evidence written to " + evidence);
return 0;

bool Decide(string prompt)
{
    Console.WriteLine(prompt);
    if (auto) { Console.WriteLine("Explicit offline simulated approval"); return true; }
    Console.Write("Type approve to accept: "); return Console.ReadLine() == "approve";
}
async Task Complete(string id, Engine activeEngine)
{
    for (var tick = 0; tick < 100; tick++)
    {
        var current = service.Get(id);
        if (current.Status == RunStatus.Completed) return;
        if (current.Status is RunStatus.RolledBack or RunStatus.Stopped) throw new InvalidOperationException("Run ended: " + current.Status);
        if (current.FindingsProposal is { } findings)
        {
            Save($"findings/proposal-r{current.Revision}.json", findings);
            if (!Decide(JsonSerializer.Serialize(findings, JsonStore.Json))) throw new InvalidOperationException("Remediation not approved");
            service.DecideFindings(id, current.Revision, true, "Explicit review of findings-derived plan", "demo-approver");
            Save($"findings/accepted-r{current.Revision + 1}.json", service.Get(id));
            continue;
        }
        if (current.FallbackProposal is { } fallback)
        {
            Save("fallback/proposal.json", fallback);
            if (!Decide(JsonSerializer.Serialize(fallback, JsonStore.Json))) throw new InvalidOperationException("Fallback not approved");
            service.DecideFallback(id, current.Revision, true, "Explicitly approve alternate provider for one bounded attempt", "demo-approver");
            continue;
        }
        foreach (var node in current.Nodes.Where(n => n.Status == NodeStatus.Pending && n.Spec.Approval && !n.Approved && WorkflowService.Ready(current, n)))
        {
            var evidenceText = string.Join("\n", node.Spec.DependsOn.Select(dep => current.Nodes.Single(n => n.Spec.Id == dep).Output));
            if (!Decide($"Run {id}, revision {current.Revision}, gate {node.Spec.Id}\n{current.Context}\n{evidenceText}"))
            {
                service.Approve(id, node.Spec.Id, current.Revision, false, "Demo user rejected gate", "demo-approver");
                await activeEngine.TickAsync(); throw new InvalidOperationException("Gate rejected");
            }
            service.Approve(id, node.Spec.Id, current.Revision, true,
                auto ? "Explicit offline simulated approval" : "Human reviewed evidence", "demo-approver");
        }
        Console.WriteLine($"Dispatch revision {current.Revision}: {string.Join(",", current.Nodes.Where(n => n.Status == NodeStatus.Pending).Select(n => n.Spec.Id))}");
        await activeEngine.TickAsync(); await Task.Delay(150);
    }
    throw new TimeoutException("Demo tick budget exceeded");
}
void Save<T>(string relative, T value)
{
    var target = Path.Combine(evidence, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllText(target, JsonSerializer.Serialize(value, JsonStore.Json));
}
void ExportRevision(string id, int revision)
{
    var current = service.Get(id); Save($"revision-{revision}/workflow.json", current);
    var workspace = agent.Workspace(id, revision);
    foreach (var file in new[] { "patch.json", "change.diff", "validation.json", "baseline-manifest.json", PatchPolicy.SourcePath, PatchPolicy.TestPath })
    {
        var target = Path.Combine(evidence, $"revision-{revision}", file); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(workspace, file), target, true);
    }
}
sealed class FailOnceAgent : IAgent
{
    private bool failed;
    public Task<string> ExecuteAsync(AgentRequest request, CancellationToken cancellation)
    {
        if (!failed) { failed = true; throw new HttpRequestException("Injected transient failure", null, HttpStatusCode.ServiceUnavailable); }
        return new DemoAgent().ExecuteAsync(request, cancellation);
    }
}
sealed class PermanentFailureAgent : IAgent
{
    public Task<string> ExecuteAsync(AgentRequest request, CancellationToken cancellation) =>
        throw new HttpRequestException("Injected authentication failure", null, HttpStatusCode.Unauthorized);
}
sealed class FindingDrivenAgent : IAgent
{
    public bool Offline => true;
    public Task<string> ExecuteAsync(AgentRequest request, CancellationToken cancellation)
    {
        if (request.Node.Id == "security" && request.Engineering?.ReservedAliases.Contains("health") != true)
            return Task.FromResult(JsonSerializer.Serialize(new SecurityReport(Quality.RequirementHash(request), "fail",
                [new("protected-alias", "high", "open", "The operational health alias is not protected from new user allocation.",
                    "Reserve health alongside admin, preserve existing stored aliases, and run case-insensitive rejection and backward-compatibility regressions.")],
                new("REQ-ALIAS-SEC", ["admin", "health"]))));
        return new DemoAgent().ExecuteAsync(request, cancellation);
    }
}
sealed class PrimaryOutageAgent : IAgent
{
    public Task<string> ExecuteAsync(AgentRequest request, CancellationToken cancellation) => request.Node.Id == "requirements"
        ? throw new HttpRequestException("Injected primary outage", null, HttpStatusCode.ServiceUnavailable)
        : new DemoAgent().ExecuteAsync(request, cancellation);
}
