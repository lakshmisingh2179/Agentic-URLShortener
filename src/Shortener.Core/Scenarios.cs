namespace Shortener.Core;

public static class Scenarios
{
    public static NodeSpec[] Create(string name)
    {
        var pre = name switch
        {
            "greenfield" => new NodeSpec("requirements", "analyst", "Specify HTTP URL shortening, expiry, redirects and abuse controls.", []),
            "brownfield" => new NodeSpec("requirements", "analyst", "Assess the supplied legacy contract and migration risks. Preserve existing short codes and redirect behavior; propose characterization tests before changes.", [], true),
            "ambiguous" => new NodeSpec("requirements", "analyst", "List unresolved retention, identity, custom alias and traffic requirements. Use only the human-approved context and explicitly label remaining assumptions.", [], true),
            _ => throw new ArgumentException("Scenario must be greenfield, brownfield or ambiguous")
        };
        return [pre,
            new("design", "architect", "Produce architecture, persistence model and API contract.", ["requirements"]),
            new("implementation", "developer", "Produce a proposed C# implementation artifact against the approved contract.", ["design"]),
            new("security", "security-reviewer", "Review URL validation, redirect abuse, authorization and persistence threats.", ["design"]),
            new("tests", "test-engineer", "Produce test cases and identify validation gaps from implementation and security findings. Do not claim to have executed code.", ["implementation", "security"]),
            new("release", "release-reviewer", "Produce a release checklist and rollback plan. This artifact is a simulated release, not a deployment.", ["tests"], true)];
    }
    public static void Validate(NodeSpec[] nodes)
    {
        if (nodes is null || nodes.Length is < 1 or > 30) throw new ArgumentException("Plan must contain 1–30 nodes");
        if (nodes.Any(n => n is null || string.IsNullOrWhiteSpace(n.Id) || n.Id.Length > 64 ||
            string.IsNullOrWhiteSpace(n.Role) || string.IsNullOrWhiteSpace(n.Task) ||
            n.Task.Length > 8000 || n.DependsOn is null || n.MaxAttempts is < 1 or > 5))
            throw new ArgumentException("Invalid node specification");
        if (nodes.Select(n => n.Id).Distinct().Count() != nodes.Length) throw new ArgumentException("Duplicate node ID");
        var map = nodes.ToDictionary(n => n.Id);
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();
        void Visit(string id)
        {
            if (!map.ContainsKey(id)) throw new ArgumentException("Missing dependency: " + id);
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new ArgumentException("Dependency cycle");
            foreach (var dep in map[id].DependsOn) Visit(dep);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var n in nodes) Visit(n.Id);
        if (!nodes.Any(n => n.Id == "release" && n.Approval)) throw new ArgumentException("Mandatory release approval gate missing");
        // Every node must be an ancestor of release, so it cannot bypass review.
        var ancestors = new HashSet<string>();
        void Collect(string id) { if (ancestors.Add(id)) foreach (var d in map[id].DependsOn) Collect(d); }
        Collect("release");
        if (ancestors.Count != nodes.Length) throw new ArgumentException("Release must depend on every node");
        if (!map.TryGetValue("security", out var security) || security.Role != "security-reviewer" || !map.ContainsKey("tests"))
            throw new ArgumentException("Security review and tests are mandatory");
        var testAncestors = new HashSet<string>();
        void TestAncestors(string id) { if (testAncestors.Add(id)) foreach (var d in map[id].DependsOn) TestAncestors(d); }
        TestAncestors("tests");
        if (!testAncestors.Contains("security")) throw new ArgumentException("Tests must wait for security review");
    }
}
