using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>
/// High-volume local execution trace for the Japanese-client hook re-acquisition build.
/// Records only data already available to this plugin through Dalamud/FFXIVClientStructs
/// and the plugin's own state. No packet interception or unrestricted process scanning.
/// </summary>
public sealed class ExecutionTraceLog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object sync = new();
    private readonly string path;
    private long sequence;
    private bool disposed;

    public string Path => path;

    public ExecutionTraceLog(string configDirectory)
    {
        var dir = System.IO.Path.Combine(configDirectory, "full-trace");
        Directory.CreateDirectory(dir);
        path = System.IO.Path.Combine(dir, $"FULL_EXECUTION_TRACE_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        Record("trace.start", new Dictionary<string, object?>
        {
            ["version"] = "0.8.1.4",
            ["client_language"] = Plugin.ClientState.ClientLanguage.ToString(),
            ["thread"] = Environment.CurrentManagedThreadId,
            ["policy_scope"] = "Dalamud public services + FFXIVClientStructs + plugin-owned state only",
        });
    }

    public void Record(string eventName, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null)
    {
        if (disposed) return;
        try
        {
            var row = new Dictionary<string, object?>
            {
                ["t"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["seq"] = Interlocked.Increment(ref sequence),
                ["event"] = eventName,
                ["thread"] = Environment.CurrentManagedThreadId,
                ["data"] = data,
                ["exception_type"] = exception?.GetType().FullName,
                ["exception_message"] = exception?.Message,
                ["stack"] = exception?.StackTrace,
            };
            var line = JsonSerializer.Serialize(row, JsonOptions);
            lock (sync)
                File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
        Record("trace.stop");
        disposed = true;
    }
}
