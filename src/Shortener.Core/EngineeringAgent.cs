using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shortener.Core;

public sealed record CommandResult(string[] Arguments, int ExitCode, long DurationMs, string Output, string OutputHash);
public sealed record ValidationReport(string RunId, int Revision, string RequirementId, string PatchHash,
    string BaselineHash, bool Success, List<CommandResult> Commands, Dictionary<string, string> FileHashes);
public sealed class EngineeringValidationException(string report) : Exception("Engineering validation failed")
{ public string Report { get; } = report; }

public sealed class EngineeringAgent : IAgent
{
    private readonly IAgent inner;
    private readonly string root;
    private readonly string dotnet;
    private readonly IReadOnlyDictionary<string, string> baseline;
    public string Identity => "EngineeringAgent/" + inner.Identity;
    public string Model => inner.Model;
    public bool Offline => inner.Offline;
    public EngineeringAgent(IAgent inner, string baselineDirectory, string workspaceDirectory, string dotnetExecutable)
    {
        this.inner = inner;
        root = Path.GetFullPath(workspaceDirectory);
        dotnet = Path.GetFullPath(dotnetExecutable);
        RejectLinks(root); RejectLinks(dotnet);
        if (!File.Exists(dotnet)) throw new ArgumentException("An absolute installed dotnet executable is required");
        baseline = ReadBaseline(Path.GetFullPath(baselineDirectory));
    }
    public static IReadOnlyDictionary<string, string> ReadBaseline(string directory)
    {
        RejectLinks(directory);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in new[] { "Directory.Build.props", "NuGet.Config" }) Add(Path.Combine(directory, file));
        foreach (var folder in new[] { "src", "tests" }) Walk(Path.Combine(directory, folder));
        return result;
        void Walk(string path)
        {
            RejectLinks(path);
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                if (Path.GetFileName(child) is "bin" or "obj") continue;
                RejectLinks(child);
                if (Directory.Exists(child)) Walk(child);
                else if (Path.GetExtension(child) is ".cs" or ".csproj") Add(child);
            }
        }
        void Add(string path)
        {
            RejectLinks(path);
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            result.Add(relative, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
        }
    }
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symlinks and reparse points are not allowed in engineering paths");
    }
    public string Workspace(string runId, int revision)
    {
        if (!Regex.IsMatch(runId, "^[a-f0-9]{32}$") || revision is < 1 or > 4) throw new ArgumentException("Invalid workspace identity");
        var path = Path.Combine(root, runId, "r" + revision); RejectLinks(path); return path;
    }
    private static void Write(string directory, string relative, string text)
    {
        var target = Path.GetFullPath(Path.Combine(directory, relative));
        if (!target.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Path escaped workspace");
        RejectLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, text);
    }
    public async Task<string> ExecuteAsync(AgentRequest request, CancellationToken cancellation)
    {
        if (request.Engineering is null)
            return request.ValidateBaseline && request.Node.Id == "tests"
                ? await ValidateBaseline(request, cancellation) : await inner.ExecuteAsync(request, cancellation);
        var requirement = request.Engineering;
        var expected = PatchPolicy.Create(requirement, baseline);
        var workspace = Workspace(request.RunId, request.Revision);
        if (request.Node.Id == "implementation")
        {
            EngineeringPatch patch;
            if (inner.Offline) patch = expected;
            else
            {
                var response = await inner.ExecuteAsync(request with { Node = request.Node with {
                    Task = "Return only this JSON patch object, without fences. This is the exact approved edit capability. " +
                        "It changes reserved-alias validation and adds a regression test; no other edits are authorized.\n" + JsonSerializer.Serialize(expected) } }, cancellation);
                patch = JsonSerializer.Deserialize<EngineeringPatch>(response) ?? throw new InvalidDataException("Missing patch");
            }
            PatchPolicy.Validate(patch, requirement, baseline);
            foreach (var (path, content) in baseline) Write(workspace, path, content);
            foreach (var path in new[] { PatchPolicy.SourcePath, PatchPolicy.TestPath }) Write(workspace, ".baseline/" + path, baseline[path]);
            Write(workspace, "patch.json", JsonSerializer.Serialize(patch, JsonStore.Json));
            Write(workspace, "change.diff", PatchPolicy.Diff(patch, baseline));
            Write(workspace, "baseline-manifest.json", JsonSerializer.Serialize(baseline.ToDictionary(p => p.Key, p => Trace.Hash(p.Value)), JsonStore.Json));
            // Candidate is visible for approval; tests reconstruct from the trusted baseline.
            foreach (var edit in patch.Edits) Write(workspace, edit.Path, PatchPolicy.Apply(baseline[edit.Path], edit));
            return JsonSerializer.Serialize(patch);
        }
        if (request.Node.Id != "tests") return await inner.ExecuteAsync(request, cancellation);
        var submitted = JsonSerializer.Deserialize<EngineeringPatch>(request.Dependencies["implementation"])
            ?? throw new InvalidDataException("Missing implementation patch");
        PatchPolicy.Validate(submitted, requirement, baseline);
        var commands = new List<CommandResult>();
        var success = false;
        try
        {
            // Existing files are checked before writes. No model commands, project files,
            // environment variables or paths are accepted. Each validation starts cleanly.
            foreach (var (path, content) in baseline) Write(workspace, path, content);
            await Required("restore", "src/Shortener.Api/Shortener.Api.csproj", "--configfile", "NuGet.Config", "--disable-parallel", "-m:1");
            await Required("restore", "tests/Shortener.Tests/Shortener.Tests.csproj", "--configfile", "NuGet.Config", "--disable-parallel", "-m:1");
            await Build();
            await Required("tests/Shortener.Tests/bin/Release/net10.0/Shortener.Tests.dll", "--integration");
            var regression = submitted.Edits.Single(e => e.Path == PatchPolicy.TestPath);
            Write(workspace, regression.Path, PatchPolicy.Apply(baseline[regression.Path], regression));
            await Required("build", "tests/Shortener.Tests/Shortener.Tests.csproj", "-c", "Release", "--no-restore", "-m:1", "/p:UseSharedCompilation=false");
            var red = await Run("tests/Shortener.Tests/bin/Release/net10.0/Shortener.Tests.dll", "--integration");
            if (red.ExitCode != 1 || !red.Output.Contains("FAIL Brownfield reserved-alias regression", StringComparison.Ordinal) ||
                !Regex.IsMatch(red.Output, @"RESULT: \d+/\d+ passed; 1 failed"))
                throw new InvalidDataException("New regression did not fail for the expected reason");
            var implementation = submitted.Edits.Single(e => e.Path == PatchPolicy.SourcePath);
            Write(workspace, implementation.Path, PatchPolicy.Apply(baseline[implementation.Path], implementation));
            await Build();
            await Required("tests/Shortener.Tests/bin/Release/net10.0/Shortener.Tests.dll", "--integration");
            success = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            commands.Add(new(["validation-error"], -1, 0, ex.GetType().Name, Trace.Hash(ex.GetType().Name)));
        }
        var report = new ValidationReport(request.RunId, request.Revision, requirement.Id, Trace.HashJson(submitted),
            submitted.BaselineHash, success, commands, submitted.Edits.ToDictionary(e => e.Path, e => Trace.Hash(File.ReadAllText(Path.Combine(workspace, e.Path)))));
        var reportJson = JsonSerializer.Serialize(report, JsonStore.Json);
        Write(workspace, "validation.json", reportJson);
        if (!success) throw new EngineeringValidationException(reportJson);
        return reportJson;

        async Task<CommandResult> Run(params string[] arguments)
        {
            var result = await Command(workspace, arguments, cancellation); commands.Add(result); return result;
        }
        async Task Required(params string[] arguments)
        { if ((await Run(arguments)).ExitCode != 0) throw new InvalidDataException("Build/test command failed"); }
        async Task Build()
        {
            await Required("build", "src/Shortener.Api/Shortener.Api.csproj", "-c", "Release", "--no-restore", "-m:1", "/p:UseSharedCompilation=false");
            await Required("build", "tests/Shortener.Tests/Shortener.Tests.csproj", "-c", "Release", "--no-restore", "-m:1", "/p:UseSharedCompilation=false");
        }
    }
    private async Task<CommandResult> Command(string workspace, string[] arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(dotnet) { WorkingDirectory = workspace, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        start.Environment.Clear();
        if (systemRoot is not null) start.Environment["SystemRoot"] = systemRoot;
        foreach (var name in new[] { "WINDIR", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "SystemDrive" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["PATH"] = Path.GetDirectoryName(dotnet)!;
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;
        start.Environment["DOTNET_HOST_PATH"] = dotnet;
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(workspace, ".runtime");
        start.Environment["APPDATA"] = Path.Combine(workspace, ".runtime", "appdata");
        start.Environment["LOCALAPPDATA"] = Path.Combine(workspace, ".runtime", "localappdata");
        start.Environment["ProgramData"] = Path.Combine(workspace, ".runtime", "programdata");
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(workspace, ".runtime", "config");
        start.Environment["NUGET_PACKAGES"] = Path.Combine(workspace, ".runtime", "packages");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        var temp = Path.Combine(workspace, ".runtime", "tmp"); Directory.CreateDirectory(temp);
        start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp; start.Environment["TMPDIR"] = temp;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start validation process");
        var watch = Stopwatch.StartNew();
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr); throw;
        }
        var output = await stdout + await stderr;
        return new(arguments, process.ExitCode, watch.ElapsedMilliseconds, output, Trace.Hash(output));
    }
    private async Task<string> ValidateBaseline(AgentRequest request, CancellationToken cancellation)
    {
        var workspace = Workspace(request.RunId, request.Revision);
        foreach (var (path, content) in baseline) Write(workspace, path, content);
        foreach (var path in new[] { PatchPolicy.SourcePath, PatchPolicy.TestPath }) Write(workspace, ".baseline/" + path, baseline[path]);
        Write(workspace, "baseline-manifest.json", JsonSerializer.Serialize(baseline.ToDictionary(p => p.Key, p => Trace.Hash(p.Value)), JsonStore.Json));
        var commands = new List<CommandResult>();
        string[][] sequence = [
            ["restore", "src/Shortener.Api/Shortener.Api.csproj", "--configfile", "NuGet.Config", "--disable-parallel", "-m:1"],
            ["restore", "tests/Shortener.Tests/Shortener.Tests.csproj", "--configfile", "NuGet.Config", "--disable-parallel", "-m:1"],
            ["build", "src/Shortener.Api/Shortener.Api.csproj", "-c", "Release", "--no-restore", "-m:1", "/p:UseSharedCompilation=false"],
            ["build", "tests/Shortener.Tests/Shortener.Tests.csproj", "-c", "Release", "--no-restore", "-m:1", "/p:UseSharedCompilation=false"],
            ["tests/Shortener.Tests/bin/Release/net10.0/Shortener.Tests.dll", "--integration"] ];
        foreach (var arguments in sequence)
        {
            var result = await Command(workspace, arguments, cancellation); commands.Add(result);
            if (result.ExitCode != 0) break;
        }
        var success = commands.Count == sequence.Length && commands.All(c => c.ExitCode == 0);
        var report = JsonSerializer.Serialize(new { request.RunId, request.Revision, requirementHash = Quality.RequirementHash(request),
            request.Contract, success, commands, baselineHash = Trace.HashJson(baseline.OrderBy(p => p.Key).ToArray()),
            scope = "Actual validation of the included from-scratch implementation; no claim of model-generated greenfield source",
            acceptanceMapping = new Dictionary<string,string> {
                ["AC-URL"] = "HTTP URL validation rejects credentials and unsafe schemes",
                ["AC-REDIRECT"] = "HTTP: authorization, redirects, gates, audit and metrics",
                ["AC-EXPIRY"] = "Expiry and unknown codes do not redirect or count clicks",
                ["AC-ALIAS"] = "Alias conflict and invalid alias are rejected atomically",
                ["AC-AUTH"] = "HTTP: authorization, redirects, gates, audit and metrics" } }, JsonStore.Json);
        Write(workspace, "validation.json", report);
        if (!success) throw new EngineeringValidationException(report);
        return report;
    }
    private static async Task<string> Drain(StreamReader reader)
    {
        var output = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer)) != 0)
            if (output.Length < 16000) output.Append(buffer, 0, Math.Min(read, 16000 - output.Length));
        return output.ToString();
    }
    public void InvalidateWorkspace(string runId, int revision)
    {
        var workspace = Workspace(runId, revision);
        if (!Directory.Exists(workspace)) return;
        // Restore the archived baseline, including after restart with a changed checkout.
        var manifestPath = Path.Combine(workspace, "baseline-manifest.json"); RejectLinks(manifestPath);
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath))!;
        foreach (var path in new[] { PatchPolicy.SourcePath, PatchPolicy.TestPath })
        {
            var archivedPath = Path.Combine(workspace, ".baseline", path); RejectLinks(archivedPath);
            var content = File.ReadAllText(archivedPath);
            if (Trace.Hash(content) != manifest[path]) throw new InvalidDataException("Archived baseline hash mismatch; manual intervention required");
            Write(workspace, path, content);
        }
        Write(workspace, "INVALIDATED.json", JsonSerializer.Serialize(new { runId, revision,
            at = DateTimeOffset.UtcNow, reason = "Rollback or superseded requirement; patch and validation retained as historical evidence" }));
    }
}
