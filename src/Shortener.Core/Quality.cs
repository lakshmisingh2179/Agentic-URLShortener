using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shortener.Core;

public static class Quality
{
    public static readonly Dictionary<string, string> SupportedDecisions = new() {
        ["creation"] = "operator-only", ["retention"] = "optional-expiry", ["aliases"] = "case-sensitive" };
    public static ScopeContract Contract(string scenario) => new(scenario, new() {
        ["AC-URL"] = "Reject non-HTTP(S), embedded credentials and control characters.",
        ["AC-REDIRECT"] = "Persist links; return 302 with original destination and increment clicks.",
        ["AC-EXPIRY"] = "Return 404 for missing/expired codes; reject past expiry on creation.",
        ["AC-ALIAS"] = "Unique case-sensitive aliases; duplicate creation returns 409.",
        ["AC-AUTH"] = "Operator may create; approver cannot create; only approver may approve gates."
    }, SupportedDecisions.ToDictionary(p => p.Key, p => scenario == "ambiguous" ? null : (string?)p.Value));
    public static string RequirementHash(AgentRequest request) => Trace.HashJson(new { request.Context, request.Engineering, request.Contract });
    public static SecurityReport Pass(AgentRequest request) => new(RequirementHash(request), "pass", []);
    public static SecurityReport Parse(string output, AgentRequest request)
    {
        var report = JsonSerializer.Deserialize<SecurityReport>(output)
            ?? throw new InvalidDataException("Missing structured security report");
        if (report.RequirementHash != RequirementHash(request) || report.Verdict is not ("pass" or "fail") ||
            report.Findings is null || report.Findings.Length > 4 || report.Findings.Any(f => f is null ||
                !Regex.IsMatch(f.Id ?? "", "^[a-z0-9-]{1,30}$") || f.Severity is not ("critical" or "high" or "medium" or "low") ||
                f.Status is not ("open" or "resolved") || string.IsNullOrWhiteSpace(f.Evidence) || f.Evidence.Length > 2000 ||
                string.IsNullOrWhiteSpace(f.Remediation) || f.Remediation.Length > 4000) ||
            report.Findings.Select(f => f.Id).Distinct().Count() != report.Findings.Length)
            throw new InvalidDataException("Invalid or stale security report");
        if (report.RecommendedEngineering is not null) PatchPolicy.ValidateRequirement(report.RecommendedEngineering);
        return report;
    }
    public static bool Blocks(SecurityReport report) => report.Verdict == "fail" ||
        report.Findings.Any(f => f.Status == "open" && f.Severity is "critical" or "high");
    public static FindingsProposal Propose(Workflow run, ArtifactRecord artifact, SecurityReport report)
    {
        var open = report.Findings.Where(f => f.Status == "open").ToArray();
        if (open.Length == 0 || run.Revision >= 4) throw new InvalidDataException("Blocked review has no actionable findings or revision budget");
        if (run.Engineering is null && report.RecommendedEngineering is not null)
            throw new InvalidDataException("Findings cannot enable an unconfigured execution capability");
        var remediation = open.Select(f => new NodeSpec("remediate-" + f.Id, "remediation-planner",
            $"Resolve finding {f.Id}: {f.Evidence}\nRequired work: {f.Remediation}\nExplain validation evidence and remaining limitations.", ["design"])).ToArray();
        var baseNodes = run.Nodes.Select(n => n.Spec).Where(n => !n.Id.StartsWith("remediate-", StringComparison.Ordinal)).ToArray();
        var plan = baseNodes.Select(n => n.Id is "security" or "implementation" ? n with { DependsOn = ["design", .. remediation.Select(x => x.Id)] } : n).Concat(remediation).ToArray();
        Scenarios.Validate(plan);
        return new(run.Revision, artifact.Id, artifact.Sha256, open.Select(f => f.Id).ToArray(), plan,
            report.RecommendedEngineering ?? run.Engineering, "Agent findings require remediation and a fresh security review; no critical/high waiver is allowed");
    }
}
