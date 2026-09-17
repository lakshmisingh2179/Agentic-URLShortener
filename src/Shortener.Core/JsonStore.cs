using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shortener.Core;

// One writer process. Mutations use copy-on-write: failed validation or IO never
// leaks a partially changed object into the in-memory committed state.
public sealed class JsonStore : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object sync = new();
    private readonly string path;
    private readonly FileStream ownership;
    private Database state;
    public JsonStore(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "state.json");
        ownership = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            state = File.Exists(path)
                ? JsonSerializer.Deserialize<Database>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("Empty database")
                : new Database();
            if (!Trace.VerifyAudit(state.Audit)) throw new InvalidDataException("Audit chain invalid or legacy unsealed state; use a fresh data directory and retain the old store for inspection");
        }
        catch { ownership.Dispose(); throw; }
    }
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    public T Read<T>(Func<Database, T> read) { lock (sync) return Copy(read(state)); }
    public T Change<T>(Func<Database, T> change)
    {
        lock (sync)
        {
            var next = Copy(state);
            var result = change(next);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            state = next;
            return Copy(result);
        }
    }
    public void Dispose() => ownership.Dispose();
}
