using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Shortener.Core;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 128 * 1024);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var demo = builder.Configuration["AGENT_MODE"] ?? "offline";
if (demo is not ("offline" or "live")) throw new InvalidOperationException("AGENT_MODE must be offline or live");
if (demo == "live" && (string.IsNullOrWhiteSpace(builder.Configuration["OPENAI_API_KEY"]) ||
    string.IsNullOrWhiteSpace(builder.Configuration["OPENAI_MODEL"])))
    throw new InvalidOperationException("Live mode requires OPENAI_API_KEY and OPENAI_MODEL");
var operatorKey = builder.Configuration["OPERATOR_KEY"] ?? "";
var approverKey = builder.Configuration["APPROVER_KEY"] ?? "";
if (operatorKey.Length < 16 || approverKey.Length < 16 || operatorKey == approverKey)
    throw new InvalidOperationException("Set distinct OPERATOR_KEY and APPROVER_KEY values of at least 16 characters");
builder.Services.AddSingleton(new JsonStore(builder.Configuration["DATA_DIR"] ?? "data"));
builder.Services.AddSingleton<WorkflowService>();
builder.Services.AddSingleton<LinkService>();
builder.Services.AddSingleton<IAgent>(_ =>
{
    IAgent agent = demo == "offline" ? new DemoAgent() : new LiveAgent(
        new HttpClient { Timeout = TimeSpan.FromSeconds(65) }, builder.Configuration["OPENAI_API_KEY"]!, builder.Configuration["OPENAI_MODEL"]!);
    return builder.Configuration["ENGINEERING_BASELINE"] is { Length: > 0 } baseline
        ? new EngineeringAgent(agent, baseline,
            builder.Configuration["ENGINEERING_ROOT"] ?? throw new InvalidOperationException("ENGINEERING_ROOT required"),
            builder.Configuration["DOTNET_HOST_PATH"] ?? throw new InvalidOperationException("DOTNET_HOST_PATH required")) : agent;
});
builder.Services.AddSingleton(s =>
{
    IAgent? fallback = demo == "offline" && builder.Configuration["ENABLE_OFFLINE_FALLBACK"] == "true" ? new DemoAgent() : null;
    if (demo == "live" && builder.Configuration["OPENAI_FALLBACK_MODEL"] is { Length: > 0 } model)
    {
        if (model == builder.Configuration["OPENAI_MODEL"]) throw new InvalidOperationException("Fallback model must differ from primary");
        fallback = new LiveAgent(new HttpClient(), builder.Configuration["OPENAI_API_KEY"]!, model);
    }
    if (fallback is not null && builder.Configuration["ENGINEERING_BASELINE"] is { Length: > 0 } baseline)
        fallback = new EngineeringAgent(fallback, baseline, builder.Configuration["ENGINEERING_ROOT"]!, builder.Configuration["DOTNET_HOST_PATH"]!);
    return new Engine(s.GetRequiredService<JsonStore>(), s.GetRequiredService<IAgent>(),
        int.TryParse(builder.Configuration["MAX_PARALLELISM"], out var p) ? p : 3, fallback: fallback);
});
builder.Services.AddHostedService<Worker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Cache-Control"] = "no-store";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path == "/metrics")
    {
        var approval = context.Request.Method == "POST" &&
            (context.Request.Path.Value!.EndsWith("/approval", StringComparison.Ordinal) ||
             context.Request.Path.Value.EndsWith("/plan-decision", StringComparison.Ordinal) ||
             context.Request.Path.Value.EndsWith("/requirement-decision", StringComparison.Ordinal) ||
             context.Request.Path.Value.EndsWith("/findings-decision", StringComparison.Ordinal) ||
             context.Request.Path.Value.EndsWith("/fallback-decision", StringComparison.Ordinal));
        var supplied = context.Request.Headers["X-Api-Key"].ToString();
        bool Match(string expected) => CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
        var isApprover = Match(approverKey);
        if (approval ? !isApprover : !(Match(operatorKey) || context.Request.Method == "GET" && isApprover))
        {
            context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "Valid role key required" }); return;
        }
        context.Items["actor"] = isApprover ? "approver" : "operator";
    }
    try { await next(context); }
    catch (Exception e) when (e is ArgumentException or Conflict or KeyNotFoundException)
    {
        context.Response.StatusCode = e is Conflict ? 409 : e is KeyNotFoundException ? 404 : 400;
        await context.Response.WriteAsJsonAsync(new { error = e is KeyNotFoundException ? "Not found" : e.Message });
    }
});
app.MapGet("/", () => Results.Content("""
    <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
    <title>URL Shortener · SDLC Lab</title><style>body{font:18px system-ui;max-width:760px;margin:8vh auto;padding:24px;background:#101827;color:#e2e8f0}code{color:#71dfb2}li{margin:14px 0}</style>
    <h1>URL Shortener · SDLC Lab</h1><p>A persistent URL service and governed agent workflow API.</p>
    <ul><li><code>POST /api/links</code> creates a short link.</li><li><code>GET /s/{code}</code> redirects.</li>
    <li><code>POST /api/runs</code> starts a greenfield, brownfield or ambiguous workflow.</li>
    <li><code>GET /api/runs/{id}</code> exposes graph status and reviewable artifacts.</li></ul>
    <p>Use the included demo script and README for approval gates, replanning, audit and metrics.</p>
    </html>
    """, "text/html"));
app.MapGet("/health", () => new { status = "ok", agentMode = demo });
app.MapPost("/api/links", (CreateLink input, LinkService service) =>
{
    var link = service.Create(input.Url, input.Alias, input.ExpiresAt);
    return Results.Created($"/api/links/{link.Code}", new { link.Code, link.Url, shortPath = $"/s/{link.Code}", link.ExpiresAt });
});
app.MapGet("/api/links/{code}", (string code, JsonStore store) => store.Read(db => db.Links[code]));
app.MapGet("/s/{code}", (string code, LinkService service) => service.Resolve(code) is { } link
    ? Results.Redirect(link.Url, permanent: false) : Results.NotFound());
app.MapPost("/api/runs", (CreateRun input, WorkflowService service, IAgent agent, HttpContext context) =>
{
    if ((input.Engineering is not null || input.ValidateBaseline) && agent is not EngineeringAgent) throw new ArgumentException("Configure engineering baseline, root and SDK before requesting execution");
    var run = service.Create(input.Scenario, input.Context, (string)context.Items["actor"]!, input.Engineering, input.ValidateBaseline);
    return Results.Created($"/api/runs/{run.Id}", run);
});
app.MapGet("/api/runs", (JsonStore store) => store.Read(db => db.Runs.Values.ToArray()));
app.MapGet("/api/runs/{id}", (string id, WorkflowService service) => service.Get(id));
app.MapPost("/api/runs/{id}/approval", (string id, GateDecision input, WorkflowService service) =>
    service.Approve(id, input.NodeId, input.Revision, input.Accept, input.Reason, "approver"));
app.MapPost("/api/runs/{id}/stop", (string id, WorkflowService service) => service.Stop(id, "operator"));
app.MapPost("/api/runs/{id}/rollback", (string id, WorkflowService service) => service.Rollback(id, "operator"));
app.MapPost("/api/runs/{id}/replan", (string id, Replan input, WorkflowService service) =>
    service.Propose(id, input.Nodes, input.Reason, input.Revision, "operator"));
app.MapPost("/api/runs/{id}/plan-decision", (string id, PlanDecision input, WorkflowService service) =>
    service.DecidePlan(id, input.Revision, input.Accept, input.Reason, "approver"));
app.MapPost("/api/runs/{id}/requirement-change", (string id, RequirementChange input, WorkflowService service) =>
    service.ProposeRequirement(id, input.Context, input.Engineering, input.Reason, input.Revision, "operator"));
app.MapPost("/api/runs/{id}/requirement-decision", (string id, PlanDecision input, WorkflowService service) =>
    service.DecideRequirement(id, input.Revision, input.Accept, input.Reason, "approver"));
app.MapPost("/api/runs/{id}/findings-decision", (string id, PlanDecision input, WorkflowService service) =>
    service.DecideFindings(id, input.Revision, input.Accept, input.Reason, "approver"));
app.MapPost("/api/runs/{id}/fallback-decision", (string id, PlanDecision input, WorkflowService service) =>
    service.DecideFallback(id, input.Revision, input.Accept, input.Reason, "approver"));
app.MapPost("/api/runs/{id}/clarifications", (string id, Clarification input, WorkflowService service) =>
    service.Clarify(id, input.Revision, input.Answers, "operator"));
app.MapGet("/api/reliability", (JsonStore store) => store.Read(Reliability.Calculate));
app.MapGet("/api/audit-integrity", (JsonStore store) => store.Read(db => new {
    valid = Trace.VerifyAudit(db.Audit), events = db.Audit.Count, head = db.Audit.LastOrDefault()?.Hash }));
app.MapGet("/api/runs/{id}/audit", (string id, JsonStore store) => store.Read(db => db.Audit.Where(a => a.RunId == id).ToArray()));
app.MapGet("/metrics", (JsonStore store) => Results.Text(store.Read(db =>
    Reliability.Prometheus(db) +
    $"sdlc_runs_total {db.Runs.Count}\n" +
    $"sdlc_attempts_total {db.Audit.Count(a => a.Action == "attempt-started")}\n" +
    $"sdlc_retries_total {db.Audit.Count(a => a.Action == "retry-scheduled")}\n" +
    $"sdlc_failures_total {db.Audit.Count(a => a.Action == "node-failed")}\n" +
    $"sdlc_running_nodes {db.Runs.Values.Sum(r => r.Nodes.Count(n => n.Status == NodeStatus.Running))}\n" +
    $"sdlc_agent_duration_ms_total {db.Runs.Values.Sum(r => r.Nodes.Sum(n => n.DurationMs) + r.History.Sum(h => h.Nodes.Sum(n => n.DurationMs)))}\n" +
    $"sdlc_approvals_total {db.Audit.Count(a => a.Action == "gate-approved")}\n" +
    $"sdlc_replans_total {db.Audit.Count(a => a.Action == "replan-approved")}\n" +
    $"shortener_links_total {db.Links.Count}\nshortener_clicks_total {db.Links.Values.Sum(l => l.Clicks)}\n"), "text/plain; version=0.0.4"));
app.Run();

record CreateLink(string Url, string? Alias = null, DateTimeOffset? ExpiresAt = null);
record CreateRun(string Scenario, string Context, EngineeringRequirement? Engineering = null, bool ValidateBaseline = false);
record Clarification(int Revision, Dictionary<string, string> Answers);
record RequirementChange(string Context, EngineeringRequirement? Engineering, string Reason, int Revision);
record GateDecision(string NodeId, int Revision, bool Accept, string Reason);
record Replan(NodeSpec[] Nodes, string Reason, int Revision);
record PlanDecision(int Revision, bool Accept, string Reason);
sealed class Worker(Engine engine, ILogger<Worker> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            engine.Recover();
            while (!stoppingToken.IsCancellationRequested)
            {
                await engine.TickAsync(stoppingToken);
                await Task.Delay(250, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            logger.LogCritical(e, "Orchestrator stopped; shutting down to prevent operating on uncertain durable state");
            Environment.ExitCode = 1; lifetime.StopApplication();
        }
    }
}
