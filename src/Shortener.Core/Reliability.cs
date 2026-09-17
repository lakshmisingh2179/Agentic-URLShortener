using System.Text.Json;

namespace Shortener.Core;

public sealed record ReliabilityReport(int Runs, int TerminalRuns, double? SuccessRate,
    double? RetryFrequency, double? RollbackFrequency, int RecoverySamples, double? MeanRecoveryMs,
    int CompletionSamples, double? MeanEndToEndMs, int IncidentRecoverySamples, double? MeanObservedRecoveryMs, int FallbackExecutions);
public static class Reliability
{
    public static string Prometheus(Database db)
    {
        var r = Calculate(db);
        string Value(double? value) => value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "NaN";
        return $"sdlc_success_rate {Value(r.SuccessRate)}\n" +
            $"sdlc_retry_frequency {Value(r.RetryFrequency)}\n" +
            $"sdlc_rollback_frequency {Value(r.RollbackFrequency)}\n" +
            $"sdlc_mean_recovery_ms {Value(r.MeanRecoveryMs)}\nsdlc_recovery_samples {r.RecoverySamples}\n" +
            $"sdlc_mean_end_to_end_ms {Value(r.MeanEndToEndMs)}\nsdlc_completion_samples {r.CompletionSamples}\n" +
            $"sdlc_mean_observed_recovery_ms {Value(r.MeanObservedRecoveryMs)}\nsdlc_incident_recovery_samples {r.IncidentRecoverySamples}\n" +
            $"sdlc_fallback_executions {r.FallbackExecutions}\n";
    }
    public static ReliabilityReport Calculate(Database db)
    {
        var terminal = db.Runs.Values.Where(r => r.Status is RunStatus.Completed or RunStatus.Stopped or RunStatus.RolledBack).ToArray();
        var recoveries = db.Audit.Where(a => a.Action == "recovery-completed").Select(a =>
            JsonDocument.Parse(a.Detail).RootElement.GetProperty("recoveryMs").GetDouble()).ToArray();
        var durations = db.Audit.Where(a => a.Action == "completed").Select(end =>
        {
            var start = db.Audit.Last(a => a.RunId == end.RunId && a.Sequence < end.Sequence &&
                a.Action is "created" or "requirement-change-approved" or "findings-plan-approved");
            return (end.At - start.At).TotalMilliseconds;
        }).ToArray();
        var incidents = db.Audit.Where(a => a.Action == "incident-recovered").Select(a =>
            JsonDocument.Parse(a.Detail).RootElement.GetProperty("recoveryMs").GetDouble()).ToArray();
        double? Fraction(int count, int total) => total == 0 ? null : (double)count / total;
        return new(db.Runs.Count, terminal.Length, Fraction(terminal.Count(r => r.Status == RunStatus.Completed), terminal.Length),
            Fraction(db.Audit.Where(a => a.Action == "retry-scheduled").Select(a => a.RunId).Distinct().Count(), db.Runs.Count),
            Fraction(db.Audit.Where(a => a.Action == "rolled-back").Select(a => a.RunId).Distinct().Count(), db.Runs.Count),
            recoveries.Length, recoveries.Length == 0 ? null : recoveries.Average(),
            durations.Length, durations.Length == 0 ? null : durations.Average(),
            incidents.Length, incidents.Length == 0 ? null : incidents.Average(),
            db.Runs.Values.Sum(r => r.Nodes.Sum(n => n.FallbackAttempts) + r.History.Sum(h => h.Nodes.Sum(n => n.FallbackAttempts))));
    }
}
