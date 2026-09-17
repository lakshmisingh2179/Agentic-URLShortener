using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Shortener.Core;
using Trace = Shortener.Core.Trace;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
Test("HTTP URL validation rejects credentials and unsafe schemes", () =>
{
    using var f = new Fixture();
    foreach (var url in new[] { "javascript:alert(1)", "ftp://example.com", "/relative", "https://u:p@example.com", "https://a.com\n" })
        Throws<ArgumentException>(() => f.Links.Create(url));
});
Test("Alias conflict and invalid alias are rejected atomically", () =>
{
    using var f = new Fixture();
    f.Links.Create("https://example.com/a", "demo");
    Throws<Conflict>(() => f.Links.Create("https://example.com/b", "demo"));
    Throws<ArgumentException>(() => f.Links.Create("https://example.com", "../x"));
    Check(f.Store.Read(d => d.Links.Count) == 1);
});
Test("Expiry and unknown codes do not redirect or count clicks", () =>
{
    using var f = new Fixture();
    Throws<ArgumentException>(() => f.Links.Create("https://example.com", expiresAt: DateTimeOffset.UtcNow.AddDays(-1)));
    var link = f.Links.Create("https://example.com", "expire");
    f.Store.Change(d => d.Links[link.Code].ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1));
    Check(f.Links.Resolve(link.Code) is null && f.Links.Resolve("missing") is null);
    Check(f.Store.Read(d => d.Links[link.Code].Clicks) == 0);
});
Test("Persistence survives reopen and counts clicks", () =>
{
    using var f = new Fixture();
    var code = f.Links.Create("https://example.com/path?q=one").Code;
    f.Links.Resolve(code); f.Store.Dispose();
    using var reopened = new JsonStore(f.Directory);
    Check(reopened.Read(d => d.Links[code].Clicks) == 1);
});
Test("Second writer is refused", () =>
{
    using var f = new Fixture();
    Throws<IOException>(() => { using var other = new JsonStore(f.Directory); });
});
Test("Failed transaction cannot leak partial mutations", () =>
{
    using var f = new Fixture();
    Throws<ArgumentException>(() => f.Store.Change<int>(d => { d.Log("x", "test", "bad", ""); throw new ArgumentException(); }));
    Check(f.Store.Read(d => d.Audit.Count) == 0);
});
Test("Corrupt state fails closed", () =>
{
    using var f = new Fixture(); f.Store.Dispose();
    File.WriteAllText(Path.Combine(f.Directory, "state.json"), "{broken");
    Throws<JsonException>(() => { using var other = new JsonStore(f.Directory); });
});
AsyncTest("Concurrent short-code creation retains every link", async () =>
{
    using var f = new Fixture();
    await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => f.Links.Create("https://example.com"))));
    Check(f.Store.Read(d => d.Links.Count) == 40);
});
Test("Graph validator rejects cycles, dangling and duplicate IDs", () =>
{
    var plan = Scenarios.Create("greenfield");
    Throws<ArgumentException>(() => Scenarios.Validate([.. plan, plan[0]]));
    var missing = plan.ToArray(); missing[0] = missing[0] with { DependsOn = ["absent"] };
    Throws<ArgumentException>(() => Scenarios.Validate(missing));
    var cycle = plan.ToArray(); cycle[0] = cycle[0] with { DependsOn = ["release"] };
    Throws<ArgumentException>(() => Scenarios.Validate(cycle));
});
Test("Graph cannot remove release approval or bypass it", () =>
{
    var plan = Scenarios.Create("greenfield");
    Throws<ArgumentException>(() => Scenarios.Validate(plan.Select(n => n with { Approval = false }).ToArray()));
    Throws<ArgumentException>(() => Scenarios.Validate([.. plan, new("orphan", "dev", "task", [])]));
});
foreach (var scenario in new[] { "greenfield", "brownfield", "ambiguous" })
    AsyncTest($"{scenario} completes only after explicit human gates", async () =>
    {
        using var f = new Fixture(); var r = f.Service.Create(scenario, "Approved demo scope", "test");
        var engine = new Engine(f.Store, new DemoAgent());
        await engine.TickAsync();
        if (scenario != "greenfield")
        {
            Check(f.Service.Get(r.Id).Nodes.All(n => n.Attempts == 0));
            if (scenario == "ambiguous") f.Service.Clarify(r.Id, 1, Quality.SupportedDecisions, "operator");
            f.Service.Approve(r.Id, "requirements", 1, true, "Legacy/ambiguity context reviewed", "human");
        }
        for (var i = 0; i < 6; i++) await engine.TickAsync();
        Check(f.Service.Get(r.Id).Nodes.Single(n => n.Spec.Id == "release").Attempts == 0);
        f.Service.Approve(r.Id, "release", 1, true, "Reviewed all artifacts", "human");
        await engine.TickAsync(); await engine.TickAsync();
        Check(f.Service.Get(r.Id).Status == RunStatus.Completed);
    });
Test("Approval rejects stale revision and unready gate", () =>
{
    using var f = new Fixture(); var r = f.Service.Create("ambiguous", "Questions answered", "test");
    Throws<Conflict>(() => f.Service.Approve(r.Id, "requirements", 2, true, "reviewed", "human"));
    Throws<Conflict>(() => f.Service.Approve(r.Id, "release", 1, true, "reviewed", "human"));
});
AsyncTest("Parallel fan-out runs independent nodes and joins before tests", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    var agent = new TrackingAgent(); var engine = new Engine(f.Store, agent, 2);
    await engine.TickAsync(); await engine.TickAsync(); await engine.TickAsync();
    Check(agent.Peak == 2, "Independent branches did not overlap");
    Check(f.Service.Get(r.Id).Nodes.Single(n => n.Spec.Id == "tests").Attempts == 0);
    await engine.TickAsync();
    Check(f.Service.Get(r.Id).Nodes.Single(n => n.Spec.Id == "tests").Status == NodeStatus.Succeeded);
});
AsyncTest("Transient failures consume bounded attempts and roll back", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    var engine = new Engine(f.Store, new FailingAgent(true));
    for (var i = 0; i < 3; i++)
    {
        await engine.TickAsync();
        f.Store.Change(d => d.Runs[r.Id].Nodes[0].RetryAt = DateTimeOffset.MinValue);
    }
    await engine.TickAsync();
    Check(f.Service.Get(r.Id).Status == RunStatus.RolledBack);
    Check(f.Service.Get(r.Id).Nodes[0].Attempts == 3);
    Check(f.Store.Read(d => d.Audit.Count(a => a.Action == "retry-scheduled")) == 2);
});
AsyncTest("Retry backoff prevents immediate redispatch", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    var engine = new Engine(f.Store, new FailingAgent(true));
    await engine.TickAsync(); await engine.TickAsync();
    Check(f.Service.Get(r.Id).Nodes[0].Attempts == 1);
});
AsyncTest("Permanent provider failures are not retried", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    var engine = new Engine(f.Store, new FailingAgent(false));
    await engine.TickAsync(); await engine.TickAsync();
    Check(f.Service.Get(r.Id).Status == RunStatus.RolledBack && f.Service.Get(r.Id).Nodes[0].Attempts == 1);
});
AsyncTest("Execution timeout is bounded and retryable", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    await new Engine(f.Store, new BlockingAgent(), timeoutSeconds: 1).TickAsync();
    Check(f.Service.Get(r.Id).Nodes[0].Status == NodeStatus.Pending);
    Check(f.Store.Read(d => d.Audit.Any(a => a.Action == "retry-scheduled")));
});
AsyncTest("Safe-stop discards in-flight output and prevents further dispatch", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    var agent = new ControlledAgent(); var engine = new Engine(f.Store, agent);
    var tick = engine.TickAsync(); await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    f.Service.Stop(r.Id, "operator"); agent.Finish.SetResult("late output");
    await tick; await engine.TickAsync();
    Check(f.Service.Get(r.Id).Status == RunStatus.Stopped);
    Check(f.Service.Get(r.Id).Nodes.All(n => n.Output is null));
});
Test("Crash recovery preserves consumed attempt budget", () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    f.Store.Change(d => { var n = d.Runs[r.Id].Nodes[0]; n.Status = NodeStatus.Running; n.Attempts = 3; return true; });
    f.Store.Dispose(); using var reopened = new JsonStore(f.Directory);
    new Engine(reopened, new DemoAgent()).Recover();
    Check(reopened.Read(d => d.Runs[r.Id].Status) == RunStatus.RollingBack);
    Check(reopened.Read(d => d.Runs[r.Id].Nodes[0].Attempts) == 3);
});
AsyncTest("Gate rejection compensates in reverse dependency order", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    var engine = new Engine(f.Store, new DemoAgent());
    for (var i = 0; i < 4; i++) await engine.TickAsync();
    f.Service.Approve(r.Id, "release", 1, false, "Security concerns", "human"); await engine.TickAsync();
    var after = f.Service.Get(r.Id);
    Check(after.Status == RunStatus.RolledBack);
    Check(after.Nodes.Where(n => n.Attempts > 0).All(n => n.Status == NodeStatus.Compensated && n.Output is not null));
    var events = f.Store.Read(d => d.Audit.Where(a => a.Action == "compensated").ToArray());
    Check(events.First().Detail.StartsWith("tests:") && events.Last().Detail.StartsWith("requirements:"));
});
Test("Governed replan pauses execution and requires explicit decision", () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    var plan = Scenarios.Create("greenfield"); plan[1] = plan[1] with { Task = "Add retention design" };
    f.Service.Propose(r.Id, plan, "Clarified retention", 1, "operator");
    Check(f.Service.Get(r.Id).Status == RunStatus.Paused && f.Service.Get(r.Id).Revision == 1);
    Throws<Conflict>(() => f.Service.DecidePlan(r.Id, 2, true, "reviewed", "human"));
    f.Service.DecidePlan(r.Id, 1, true, "Reviewed change", "human");
    Check(f.Service.Get(r.Id).Revision == 2 && f.Service.Get(r.Id).Nodes[1].Spec.Task == "Add retention design");
});
Test("Rejected replan retains original graph", () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    var plan = Scenarios.Create("greenfield"); plan[1] = plan[1] with { Task = "changed" };
    f.Service.Propose(r.Id, plan, "why", 1, "operator");
    f.Service.DecidePlan(r.Id, 1, false, "No need", "human");
    Check(f.Service.Get(r.Id).Revision == 1 && f.Service.Get(r.Id).Nodes[1].Spec.Task != "changed");
});
AsyncTest("Replanning cannot reset attempts or mutate completed work", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    await new Engine(f.Store, new DemoAgent()).TickAsync();
    var plan = Scenarios.Create("greenfield"); plan[0] = plan[0] with { Task = "changed" };
    Throws<Conflict>(() => f.Service.Propose(r.Id, plan, "why", 1, "operator"));
});
Test("Replan budget is capped at three accepted revisions", () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "test");
    for (var i = 1; i <= 3; i++)
    {
        f.Service.Propose(r.Id, Scenarios.Create("greenfield"), "Review", i, "operator");
        f.Service.DecidePlan(r.Id, i, true, "approved", "human");
    }
    Throws<Conflict>(() => f.Service.Propose(r.Id, Scenarios.Create("greenfield"), "Review", 4, "operator"));
});
Test("Audit sequences and actor decisions persist", () =>
{
    using var f = new Fixture(); var r = f.Service.Create("brownfield", "scope", "operator");
    f.Service.Approve(r.Id, "requirements", 1, true, "Answers reviewed", "approver");
    var audit = f.Store.Read(d => d.Audit.ToArray());
    Check(audit.Select(a => a.Sequence).SequenceEqual(new long[] { 1, 2 }));
    Check(audit[1].Actor == "approver" && audit[1].Detail.Contains("Answers reviewed"));
});
AsyncTest("Pending proposal survives restart and blocks dispatch", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    f.Service.Propose(r.Id, Scenarios.Create("greenfield"), "Review plan", 1, "operator");
    f.Store.Dispose(); using var reopened = new JsonStore(f.Directory);
    var engine = new Engine(reopened, new DemoAgent()); engine.Recover(); await engine.TickAsync();
    Check(reopened.Read(d => d.Runs[r.Id].Proposal) is not null);
    Check(reopened.Read(d => d.Runs[r.Id].Nodes.All(n => n.Attempts == 0)));
});
AsyncTest("Explicit rollback after stop retains compensated evidence", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    var engine = new Engine(f.Store, new DemoAgent()); await engine.TickAsync();
    f.Service.Stop(r.Id, "operator"); await engine.TickAsync();
    f.Service.Rollback(r.Id, "operator"); await engine.TickAsync();
    Check(f.Service.Get(r.Id).Status == RunStatus.RolledBack);
    Check(f.Service.Get(r.Id).Nodes[0].Status == NodeStatus.Compensated);
    Check(f.Service.Get(r.Id).Nodes[0].Output is not null);
});
Test("Null plan input is rejected without changing run state", () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    Throws<ArgumentException>(() => f.Service.Propose(r.Id, null!, "Review", 1, "operator"));
    Check(f.Service.Get(r.Id).Status == RunStatus.Active);
});
AsyncTest("Live adapter sends authenticated request and parses text output", async () =>
{
    var handler = new FakeHttp();
    using var http = new HttpClient(handler);
    var agent = new LiveAgent(http, "test-secret", "configured-model");
    var result = await agent.ExecuteAsync(new("run", 1, "scope", Scenarios.Create("greenfield")[0], [], "stable-key"), default);
    Check(result == "real-shaped artifact" && handler.Valid);
});

if (args.Contains("--integration"))
    AsyncTest("HTTP: authorization, redirects, gates, audit and metrics", async () =>
    {
        using var f = new Fixture();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add(Path.Combine(root, "src/Shortener.Api/bin/Release/net10.0/Shortener.Api.dll"));
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["DATA_DIR"] = Path.Combine(f.Directory, "api");
        start.Environment["OPERATOR_KEY"] = "operator-test-secret";
        start.Environment["APPROVER_KEY"] = "approver-test-secret";
        start.Environment["AGENT_MODE"] = "offline";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            var ready = false;
            for (var i = 0; i < 60; i++)
            {
                if (process.HasExited) throw new Exception("API exited: " + await stderr);
                try { if ((await http.GetAsync("/health")).IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            Check(ready, "API did not start");
            Check((await http.GetAsync("/api/runs")).StatusCode == HttpStatusCode.Unauthorized);
            http.DefaultRequestHeaders.Add("X-Api-Key", "operator-test-secret");
            var create = await http.PostAsJsonAsync("/api/links", new { url = "https://example.com/path", alias = "test" });
            Check(create.StatusCode == HttpStatusCode.Created);
            Check((await http.PostAsJsonAsync("/api/links", new { url = "https://example.com", alias = "test" })).StatusCode == HttpStatusCode.Conflict);
            var redirect = await http.GetAsync("/s/test");
            Check(redirect.StatusCode == HttpStatusCode.Found && redirect.Headers.Location?.ToString() == "https://example.com/path");
            var runResponse = await http.PostAsJsonAsync("/api/runs", new { scenario = "ambiguous", context = "HTTP only, 30 day retention, no accounts" });
            Check(runResponse.StatusCode == HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
            var id = body.RootElement.GetProperty("id").GetString();
            Check((await http.PostAsJsonAsync($"/api/runs/{id}/clarifications", new { revision = 1, answers = Quality.SupportedDecisions })).IsSuccessStatusCode);
            var decision = new { nodeId = "requirements", revision = 1, accept = true, reason = "Reviewed supplied answers" };
            Check((await http.PostAsJsonAsync($"/api/runs/{id}/approval", decision)).StatusCode == HttpStatusCode.Unauthorized);
            Check((await http.PostAsJsonAsync($"/api/runs/{id}/findings-decision", new { revision = 1, accept = true, reason = "attempt" })).StatusCode == HttpStatusCode.Unauthorized);
            Check((await http.PostAsJsonAsync($"/api/runs/{id}/fallback-decision", new { revision = 1, accept = true, reason = "attempt" })).StatusCode == HttpStatusCode.Unauthorized);
            http.DefaultRequestHeaders.Remove("X-Api-Key"); http.DefaultRequestHeaders.Add("X-Api-Key", "approver-test-secret");
            Check((await http.PostAsJsonAsync($"/api/runs/{id}/approval", decision)).IsSuccessStatusCode);
            Check((await http.GetStringAsync($"/api/runs/{id}/audit")).Contains("gate-approved"));
            Check((await http.GetStringAsync("/metrics")).Contains("shortener_clicks_total 1"));
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); await stdout; await stderr; }
    });
Test("Patch capability rejects traversal, arbitrary C# and stale baseline", () =>
{
    var baseline = new Dictionary<string, string> {
        [PatchPolicy.SourcePath] = "        return store.Change(db =>", [PatchPolicy.TestPath] = "// ENGINEERING_" + "REGRESSION_INSERTION_POINT" };
    var requirement = new EngineeringRequirement("REQ-1", ["admin"]);
    var patch = PatchPolicy.Create(requirement, baseline);
    PatchPolicy.Validate(patch, requirement, baseline);
    Throws<InvalidDataException>(() => PatchPolicy.Validate(patch with { Edits = [patch.Edits[0] with { Path = "../escape.cs" }, patch.Edits[1]] }, requirement, baseline));
    Throws<InvalidDataException>(() => PatchPolicy.Validate(patch with { Edits = [patch.Edits[0] with { Replacement = "System.IO.File.Delete(\"anything\");" }, patch.Edits[1]] }, requirement, baseline));
    Throws<InvalidDataException>(() => PatchPolicy.Apply("changed baseline", patch.Edits[0]));
    Throws<ArgumentException>(() => PatchPolicy.Create(new("REQ-1", ["admin\";evil"]), baseline));
});
AsyncTest("Requirement revision invalidates approved outputs and re-executes behind fresh gates", async () =>
{
    using var f = new Fixture(); var run = f.Service.Create("brownfield", "initial requirement", "operator");
    var engine = new Engine(f.Store, new DemoAgent());
    async Task Complete()
    {
        for (var i = 0; i < 8; i++)
        {
            var r = f.Service.Get(run.Id);
            foreach (var n in r.Nodes.Where(n => n.Spec.Approval && !n.Approved && WorkflowService.Ready(r, n)))
                f.Service.Approve(r.Id, n.Spec.Id, r.Revision, true, "reviewed", "approver");
            await engine.TickAsync();
        }
    }
    await Complete(); Check(f.Service.Get(run.Id).Status == RunStatus.Completed);
    var original = f.Service.Get(run.Id).Artifacts.Select(a => a.Sha256).ToArray();
    f.Service.ProposeRequirement(run.Id, "changed upstream requirement", null, "scope expanded", 1, "operator");
    await engine.TickAsync(); Check(f.Service.Get(run.Id).Status == RunStatus.Paused);
    f.Service.DecideRequirement(run.Id, 1, true, "change accepted", "approver");
    var revised = f.Service.Get(run.Id);
    Check(revised.Nodes.All(n => n.Output is null && !n.Approved && n.Attempts == 0));
    Check(revised.History.Count == 1 && revised.History[0].Nodes.All(n => n.Status == NodeStatus.Succeeded));
    Throws<Conflict>(() => f.Service.Approve(run.Id, "requirements", 1, true, "stale", "approver"));
    await engine.TickAsync(); Check(f.Service.Get(run.Id).Nodes.All(n => n.Attempts == 0));
    await Complete(); revised = f.Service.Get(run.Id);
    Check(revised.Status == RunStatus.Completed && revised.Revision == 2 && revised.Artifacts.Count == 12);
    Check(revised.Artifacts.Take(6).Select(a => a.Sha256).SequenceEqual(original));
    Check(revised.Artifacts.Skip(6).All(a => a.RequirementHash == Trace.RequirementHash(revised)));
});
Test("Rejected requirement change preserves approvals and completion", () =>
{
    using var f = new Fixture(); var r = f.Service.Create("brownfield", "scope", "operator");
    f.Service.Approve(r.Id, "requirements", 1, true, "yes", "approver");
    f.Service.ProposeRequirement(r.Id, "changed", null, "why", 1, "operator");
    f.Service.DecideRequirement(r.Id, 1, false, "keep old", "approver");
    Check(f.Service.Get(r.Id).Nodes[0].Approved && f.Service.Get(r.Id).Revision == 1);
});
Test("Tampered audit fails integrity verification and reopen", () =>
{
    using var f = new Fixture(); f.Service.Create("greenfield", "scope", "operator");
    Check(f.Store.Read(d => Trace.VerifyAudit(d.Audit)));
    f.Store.Change(d => { d.Audit[0] = d.Audit[0] with { Detail = "modified" }; return true; });
    Check(!f.Store.Read(d => Trace.VerifyAudit(d.Audit)));
    f.Store.Dispose(); Throws<InvalidDataException>(() => { using var reopened = new JsonStore(f.Directory); });
});
AsyncTest("Artifact trace links requirement, identity, dependencies and hashes", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    var engine = new Engine(f.Store, new DemoAgent()); await engine.TickAsync(); await engine.TickAsync();
    var artifacts = f.Service.Get(r.Id).Artifacts;
    Check(artifacts[1].InputHashes["requirements"] == artifacts[0].Sha256);
    Check(artifacts.All(a => a.Sha256 == Trace.Hash(a.Content) && a.Agent == "DemoAgent" && a.Revision == 1));
});
AsyncTest("Reliability denominators and recovery measurement are explicit", async () =>
{
    using var f = new Fixture(); Check(f.Store.Read(Reliability.Calculate).SuccessRate is null);
    var r = f.Service.Create("greenfield", "scope", "operator");
    f.Store.Change(d => { var n = d.Runs[r.Id].Nodes[0]; n.Status = NodeStatus.Running; n.Attempts = 1; return true; });
    var engine = new Engine(f.Store, new DemoAgent()); engine.Recover(); await engine.TickAsync();
    var report = f.Store.Read(Reliability.Calculate);
    Check(report.RecoverySamples == 1 && report.MeanRecoveryMs > 0 && report.TerminalRuns == 0 && report.SuccessRate is null);
    f.Service.Stop(r.Id, "operator"); await engine.TickAsync();
    Check(f.Store.Read(Reliability.Calculate).SuccessRate == 0);
    f.Service.Rollback(r.Id, "operator"); await engine.TickAsync();
    Check(f.Store.Read(Reliability.Calculate).RollbackFrequency == 1);
});
AsyncTest("Critical finding overrides pass verdict and generates approval-required plan", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    var engine = new Engine(f.Store, new FindingAgent());
    for (var i = 0; i < 3; i++) await engine.TickAsync();
    var paused = f.Service.Get(r.Id);
    Check(paused.Status == RunStatus.Paused && paused.FindingsProposal is not null);
    Check(paused.Nodes.Single(n => n.Spec.Id == "tests").Attempts == 0);
    Check(paused.FindingsProposal!.Nodes.Any(n => n.Id == "remediate-alias-gap" && n.Task.Contains("health")));
    Throws<Conflict>(() => f.Service.Approve(r.Id, "release", 1, true, "bypass", "approver"));
    f.Service.DecideFindings(r.Id, 1, true, "Address finding", "approver");
    for (var i = 0; i < 6; i++) await engine.TickAsync();
    var revised = f.Service.Get(r.Id);
    Check(revised.Revision == 2 && revised.Nodes.Single(n => n.Spec.Id == "tests").Status == NodeStatus.Succeeded);
    Check(!revised.Nodes.Single(n => n.Spec.Id == "release").Approved);
});
AsyncTest("Rejecting security remediation cannot waive the blocker", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    var engine = new Engine(f.Store, new FindingAgent());
    for (var i = 0; i < 3; i++) await engine.TickAsync();
    f.Service.DecideFindings(r.Id, 1, false, "Reject plan", "approver"); await engine.TickAsync();
    Check(f.Service.Get(r.Id).Status == RunStatus.RolledBack);
});
AsyncTest("Malformed security output fails closed", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    var engine = new Engine(f.Store, new FindingAgent(malformed: true));
    for (var i = 0; i < 4; i++) await engine.TickAsync();
    Check(f.Service.Get(r.Id).Status == RunStatus.RolledBack);
    Check(f.Service.Get(r.Id).Nodes.Single(n => n.Spec.Id == "tests").Attempts == 0);
});
Test("Graph edits cannot bypass security before tests", () =>
{
    var nodes = Scenarios.Create("greenfield").Select(n => n.Id == "tests" ? n with { DependsOn = ["implementation"] }
        : n.Id == "release" ? n with { DependsOn = ["tests", "security"] } : n).ToArray();
    Throws<ArgumentException>(() => Scenarios.Validate(nodes));
});
AsyncTest("Findings proposal and source hash survive restart", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    var engine = new Engine(f.Store, new FindingAgent()); for (var i = 0; i < 3; i++) await engine.TickAsync();
    var hash = f.Service.Get(r.Id).FindingsProposal!.SourceHash;
    f.Store.Dispose(); using var reopened = new JsonStore(f.Directory); var service = new WorkflowService(reopened);
    Check(service.Get(r.Id).FindingsProposal!.SourceHash == hash);
    Throws<Conflict>(() => service.DecideFindings(r.Id, 2, true, "stale", "approver"));
    service.DecideFindings(r.Id, 1, true, "reviewed after restart", "approver");
    Check(service.Get(r.Id).Revision == 2);
});
AsyncTest("Fallback is separately approved, bounded and attributed", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    var engine = new Engine(f.Store, new FailingAgent(true), fallback: new DemoAgent());
    for (var i = 0; i < 3; i++) { await engine.TickAsync(); f.Store.Change(d => d.Runs[r.Id].Nodes[0].RetryAt = DateTimeOffset.MinValue); }
    Check(f.Service.Get(r.Id).Status == RunStatus.Paused && f.Service.Get(r.Id).Nodes[0].FallbackAttempts == 0);
    await engine.TickAsync(); Check(f.Service.Get(r.Id).Nodes[0].FallbackAttempts == 0);
    f.Service.DecideFallback(r.Id, 1, true, "Use offline fixture explicitly", "approver"); await engine.TickAsync();
    var node = f.Service.Get(r.Id).Nodes[0];
    Check(node.Attempts == 3 && node.FallbackAttempts == 1 && node.Status == NodeStatus.Succeeded);
    Check(f.Service.Get(r.Id).Artifacts.Last().Agent == "DemoAgent");
    Check(f.Store.Read(Reliability.Calculate).IncidentRecoverySamples == 1);
});
AsyncTest("Fallback failure does not loop or reset primary budget", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    var engine = new Engine(f.Store, new FailingAgent(true), fallback: new FailingAgent(true));
    for (var i = 0; i < 3; i++) { await engine.TickAsync(); f.Store.Change(d => d.Runs[r.Id].Nodes[0].RetryAt = DateTimeOffset.MinValue); }
    f.Service.DecideFallback(r.Id, 1, true, "alternate", "approver"); await engine.TickAsync(); await engine.TickAsync();
    Check(f.Service.Get(r.Id).Status == RunStatus.RolledBack && f.Service.Get(r.Id).Nodes[0].FallbackAttempts == 1);
});
Test("Ambiguous scope requires concrete supported answers before approval", () =>
{
    using var f = new Fixture(); var r = f.Service.Create("ambiguous", "make links short", "operator");
    Throws<Conflict>(() => f.Service.Approve(r.Id, "requirements", 1, true, "yes", "approver"));
    Throws<ArgumentException>(() => f.Service.Clarify(r.Id, 1, new() { ["creation"] = "public" }, "operator"));
    var before = Trace.RequirementHash(r);
    f.Service.Clarify(r.Id, 1, Quality.SupportedDecisions, "operator");
    f.Service.Approve(r.Id, "requirements", 1, true, "answers reviewed", "approver");
    Check(Trace.RequirementHash(f.Service.Get(r.Id)) != before);
});
foreach (var change in new[] { "model", "agent", "missing", "legacy-unbound" })
    AsyncTest($"Restart blocks fallback identity change: {change}", async () =>
    {
        using var f = new Fixture();
        var r = f.Service.Create("greenfield", "scope", "operator");
        var original = new CountingIdentityAgent("approved-agent", "model-a");
        var engine = new Engine(f.Store, new FailingAgent(true), fallback: original);
        for (var i = 0; i < 3; i++) { await engine.TickAsync(); f.Store.Change(d => d.Runs[r.Id].Nodes[0].RetryAt = DateTimeOffset.MinValue); }
        f.Service.DecideFallback(r.Id, 1, true, "Approve exact model-a choice", "approver");
        Check(f.Service.Get(r.Id).Nodes[0].ApprovedFallback == new FallbackIdentity("approved-agent", "model-a"));
        if (change == "legacy-unbound") f.Store.Change(d => d.Runs[r.Id].Nodes[0].ApprovedFallback = null);
        f.Store.Dispose(); using var reopened = new JsonStore(f.Directory);
        var alternate = change switch {
            "model" => new CountingIdentityAgent("approved-agent", "model-b"),
            "agent" => new CountingIdentityAgent("different-agent", "model-a"),
            "missing" => null,
            _ => new CountingIdentityAgent("approved-agent", "model-a") };
        var primary = new CountingIdentityAgent("primary", "primary-model");
        var restarted = new Engine(reopened, primary, fallback: alternate); restarted.Recover();
        await restarted.TickAsync();
        Check((alternate?.Calls ?? 0) == 0 && original.Calls == 0 && primary.Calls == 0);
        Check(reopened.Read(d => d.Runs[r.Id].Nodes[0].FallbackAttempts) == 0);
        Check(reopened.Read(d => d.Runs[r.Id].Nodes[0].Attempts) == 3);
        Check(reopened.Read(d => d.Audit.Any(a => a.Action == "fallback-identity-mismatch")));
        await restarted.TickAsync(); Check(reopened.Read(d => d.Runs[r.Id].Status) == RunStatus.RolledBack);
    });
AsyncTest("Restart permits the exact persisted approved fallback identity", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    var engine = new Engine(f.Store, new FailingAgent(true), fallback: new CountingIdentityAgent("approved-agent", "model-a"));
    for (var i = 0; i < 3; i++) { await engine.TickAsync(); f.Store.Change(d => d.Runs[r.Id].Nodes[0].RetryAt = DateTimeOffset.MinValue); }
    f.Service.DecideFallback(r.Id, 1, true, "Approved choice", "approver");
    f.Store.Dispose(); using var reopened = new JsonStore(f.Directory);
    var alternate = new CountingIdentityAgent("approved-agent", "model-a");
    var restarted = new Engine(reopened, new FailingAgent(true), fallback: alternate); restarted.Recover();
    await restarted.TickAsync();
    Check(alternate.Calls == 1 && reopened.Read(d => d.Runs[r.Id].Nodes[0].Status) == NodeStatus.Succeeded);
    Check(reopened.Read(d => d.Runs[r.Id].Nodes[0].FallbackAttempts) == 1);
    var artifact = reopened.Read(d => d.Runs[r.Id].Artifacts.Last());
    Check(artifact.Agent == "approved-agent" && artifact.Model == "model-a");
});
AsyncTest("Approval after restart binds the original proposal, not new configuration", async () =>
{
    using var f = new Fixture(); var r = f.Service.Create("greenfield", "scope", "operator");
    var engine = new Engine(f.Store, new FailingAgent(true), fallback: new CountingIdentityAgent("approved-agent", "model-a"));
    for (var i = 0; i < 3; i++) { await engine.TickAsync(); f.Store.Change(d => d.Runs[r.Id].Nodes[0].RetryAt = DateTimeOffset.MinValue); }
    f.Store.Dispose(); using var reopened = new JsonStore(f.Directory);
    var service = new WorkflowService(reopened);
    service.DecideFallback(r.Id, 1, true, "Approve original proposal", "approver");
    Check(service.Get(r.Id).Nodes[0].ApprovedFallback == new FallbackIdentity("approved-agent", "model-a"));
    var changed = new CountingIdentityAgent("approved-agent", "model-b");
    await new Engine(reopened, new FailingAgent(true), fallback: changed).TickAsync();
    Check(changed.Calls == 0 && service.Get(r.Id).Status == RunStatus.RollingBack);
});
// ENGINEERING_REGRESSION_INSERTION_POINT
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + "\n" + ex); }
}
Console.WriteLine($"RESULT: {tests.Count - failures}/{tests.Count} passed; {failures} failed");
return failures == 0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "shortener-test-" + Guid.NewGuid().ToString("N"));
    public JsonStore Store { get; }
    public WorkflowService Service { get; }
    public LinkService Links { get; }
    public Fixture() { Store = new(Directory); Service = new(Store); Links = new(Store); }
    public void Dispose() { Store.Dispose(); System.IO.Directory.Delete(Directory, recursive: true); }
}
sealed class TrackingAgent : IAgent
{
    private int active;
    public int Peak;
    public async Task<string> ExecuteAsync(AgentRequest request, CancellationToken cancellation)
    {
        var count = Interlocked.Increment(ref active);
        int observed;
        do { observed = Peak; } while (count > observed && Interlocked.CompareExchange(ref Peak, count, observed) != observed);
        await Task.Delay(100, cancellation); Interlocked.Decrement(ref active);
        return request.Node.Id == "security" ? JsonSerializer.Serialize(Quality.Pass(request)) : "artifact";
    }
}
sealed class FailingAgent(bool transient) : IAgent
{
    public Task<string> ExecuteAsync(AgentRequest request, CancellationToken cancellation) =>
        throw new HttpRequestException("not persisted", null, transient ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized);
}
sealed class BlockingAgent : IAgent
{
    public async Task<string> ExecuteAsync(AgentRequest request, CancellationToken cancellation)
    { await Task.Delay(Timeout.Infinite, cancellation); return "never"; }
}
sealed class ControlledAgent : IAgent
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<string> ExecuteAsync(AgentRequest request, CancellationToken cancellation) { Started.SetResult(); return Finish.Task; }
}
sealed class FakeHttp : HttpMessageHandler
{
    public bool Valid;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Valid = request.RequestUri!.ToString() == "https://api.openai.com/v1/responses" &&
            request.Headers.Authorization?.Parameter == "test-secret" && body.Contains("configured-model") && body.Contains("analyst");
        return new(HttpStatusCode.OK) { Content = new StringContent("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"real-shaped artifact"}]}]}""") };
    }
}
sealed class FindingAgent(bool malformed = false) : IAgent
{
    public Task<string> ExecuteAsync(AgentRequest request, CancellationToken cancellation)
    {
        if (request.Node.Id != "security" || request.Revision > 1) return new DemoAgent().ExecuteAsync(request, cancellation);
        return Task.FromResult(malformed ? "I think this is safe" : JsonSerializer.Serialize(new SecurityReport(
            Quality.RequirementHash(request), "pass", [new("alias-gap", "critical", "open", "health alias is not reserved", "Add health policy coverage and verify existing records still resolve")] )));
    }
}
sealed class CountingIdentityAgent(string identity, string model) : IAgent
{
    public string Identity => identity;
    public string Model => model;
    public int Calls { get; private set; }
    public Task<string> ExecuteAsync(AgentRequest request, CancellationToken cancellation)
    { Calls++; return new DemoAgent().ExecuteAsync(request, cancellation); }
}
