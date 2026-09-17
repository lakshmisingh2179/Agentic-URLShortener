using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shortener.Core;

public sealed record FileEdit(string Path, string BeforeSha256, string Search, string Replacement);
public sealed record EngineeringPatch(string RequirementId, string BaselineHash, FileEdit[] Edits);

// A deliberately small capability language: only reviewed reserved-alias changes.
// Model-authored C# outside these exact templates is never compiled or executed.
public static class PatchPolicy
{
    public const string SourcePath = "src/Shortener.Core/LinkService.cs";
    public const string TestPath = "tests/Shortener.Tests/Program.cs";
    public static void ValidateRequirement(EngineeringRequirement requirement)
    {
        if (requirement is null || !Regex.IsMatch(requirement.Id ?? "", "^[A-Za-z0-9_-]{1,64}$") ||
            requirement.ReservedAliases is null || requirement.ReservedAliases.Length is < 1 or > 8 ||
            requirement.ReservedAliases.Any(a => a is null || !Regex.IsMatch(a, "^[a-z]{4,16}$")) ||
            requirement.ReservedAliases.Distinct().Count() != requirement.ReservedAliases.Length ||
            requirement.ReservedAliases.Any(a => a is "demo" or "test" or "expire" or "public" or "legacy"))
            throw new ArgumentException("Use an ID and 1–8 unique lowercase reserved aliases; fixture aliases are protected");
    }
    public static EngineeringPatch Create(EngineeringRequirement requirement, IReadOnlyDictionary<string, string> baseline)
    {
        ValidateRequirement(requirement);
        var values = string.Join(", ", requirement.ReservedAliases.Order().Select(a => JsonSerializer.Serialize(a)));
        const string sourceAnchor = "        return store.Change(db =>";
        var sourceReplacement = $"        if (alias is not null && new[] {{ {values} }}.Contains(alias, StringComparer.OrdinalIgnoreCase))\n" +
            "            throw new ArgumentException(\"Alias is reserved\");\n" + sourceAnchor;
        const string testAnchor = "// ENGINEERING_REGRESSION_INSERTION_POINT";
        var testReplacement = $$"""
Test("Brownfield reserved-alias regression", () =>
{
    using var f = new Fixture();
    foreach (var alias in new[] { {{values}} })
    {
        Throws<ArgumentException>(() => f.Links.Create("https://example.com", alias));
        Throws<ArgumentException>(() => f.Links.Create("https://example.com", alias.ToUpperInvariant()));
    }
    // Existing persisted aliases, including newly reserved names, must still resolve.
    f.Store.Change(db => { db.Links.Add("{{requirement.ReservedAliases.Order().First()}}", new Link {
        Code = "{{requirement.ReservedAliases.Order().First()}}", Url = "https://example.com/legacy", Clicks = 7 }); return true; });
    Check(f.Links.Resolve("{{requirement.ReservedAliases.Order().First()}}")?.Clicks == 8);
    Check(f.Links.Create("https://example.com", "public").Code == "public");
});
// ENGINEERING_REGRESSION_INSERTION_POINT
""";
        return new(requirement.Id, Trace.HashJson(baseline.OrderBy(p => p.Key).ToArray()), [
            new(SourcePath, Trace.Hash(baseline[SourcePath]), sourceAnchor, sourceReplacement),
            new(TestPath, Trace.Hash(baseline[TestPath]), testAnchor, testReplacement)]);
    }
    public static void Validate(EngineeringPatch patch, EngineeringRequirement requirement, IReadOnlyDictionary<string, string> baseline)
    {
        if (patch is null || JsonSerializer.Serialize(patch) != JsonSerializer.Serialize(Create(requirement, baseline)))
            throw new InvalidDataException("Patch is outside the approved capability, baseline, requirement or file allowlist");
    }
    public static string Apply(string original, FileEdit edit)
    {
        if (Trace.Hash(original) != edit.BeforeSha256 || original.Split(edit.Search, StringSplitOptions.None).Length != 2)
            throw new InvalidDataException("Stale file hash or ambiguous patch anchor");
        return original.Replace(edit.Search, edit.Replacement, StringComparison.Ordinal);
    }
    public static string Diff(EngineeringPatch patch, IReadOnlyDictionary<string, string> baseline)
    {
        var output = new System.Text.StringBuilder();
        foreach (var edit in patch.Edits)
        {
            var original = baseline[edit.Path];
            var line = original[..original.IndexOf(edit.Search, StringComparison.Ordinal)].Count(c => c == '\n') + 1;
            var before = edit.Search.Split('\n'); var after = edit.Replacement.Split('\n');
            output.AppendLine($"--- a/{edit.Path}").AppendLine($"+++ b/{edit.Path}")
                .AppendLine($"@@ -{line},{before.Length} +{line},{after.Length} @@");
            foreach (var text in before) output.Append('-').AppendLine(text);
            foreach (var text in after) output.Append('+').AppendLine(text);
        }
        return output.ToString();
    }
}
