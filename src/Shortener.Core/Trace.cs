using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shortener.Core;

public static class Trace
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string HashJson<T>(T value) => Hash(JsonSerializer.Serialize(value));
    public static string RequirementHash(Workflow run) => HashJson(new { run.Context, run.Engineering, run.Contract });
    public static bool VerifyAudit(IEnumerable<AuditEvent> events)
    {
        var previous = ""; long sequence = 0;
        foreach (var entry in events)
        {
            if (entry.Sequence != ++sequence || entry.PreviousHash != previous ||
                entry.Hash != HashJson(entry with { Hash = "" })) return false;
            previous = entry.Hash;
        }
        return true;
    }
}
