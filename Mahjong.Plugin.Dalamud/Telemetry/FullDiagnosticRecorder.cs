using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;

namespace Mahjong.Plugin.Dalamud.Telemetry;

/// <summary>
/// Policy-safe exhaustive diagnostic stream. It records only data already
/// exposed to this plugin through Dalamud/FFXIVClientStructs and the plugin's
/// own state machine. No packet interception, external-process memory access,
/// or protected-region scanning is performed.
/// </summary>
public sealed class FullDiagnosticRecorder : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        WriteIndented = false,
    };

    private readonly StateAggregator aggregator;
    private readonly InputEventLogger eventLogger;
    private readonly StreamWriter writer;
    private readonly object gate = new();
    private long sequence;
    private bool disposed;

    public string LogPath { get; }

    public FullDiagnosticRecorder(
        StateAggregator aggregator,
        InputEventLogger eventLogger,
        string pluginConfigDirectory)
    {
        this.aggregator = aggregator;
        this.eventLogger = eventLogger;
        Directory.CreateDirectory(pluginConfigDirectory);
        LogPath = Path.Combine(pluginConfigDirectory,
            $"CALL_HOOK_CAPTURE_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        writer = new StreamWriter(new FileStream(
            LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };

        aggregator.Changed += OnStateChanged;
        eventLogger.CallbackObserved += OnCallbackObserved;
        eventLogger.CallPromptObserved += OnCallPromptObserved;
        eventLogger.BeforeFireCallback += OnBeforeFireCallback;

        Write("capture_start", new
        {
            version = "0.8.1.4",
            scope = "Dalamud policy-safe full diagnostic capture",
            utc = DateTime.UtcNow,
        });
    }

    private void OnStateChanged(StateSnapshot snapshot) =>
        Write("state_snapshot", snapshot);

    private void OnCallbackObserved(InputCallbackEvent evt) =>
        Write("fire_callback_post", evt);

    private void OnCallPromptObserved(CallPromptEvent evt) =>
        Write("call_prompt", evt);

    private void OnBeforeFireCallback(string addonName) =>
        Write("fire_callback_pre", new
        {
            addonName,
            snapshot = aggregator.Latest,
        });

    public void Record(string category, object? payload) => Write(category, payload);

    private void Write(string category, object? payload)
    {
        if (disposed)
            return;
        try
        {
            var envelope = new
            {
                seq = System.Threading.Interlocked.Increment(ref sequence),
                utc = DateTime.UtcNow,
                category,
                payload,
            };
            string json = JsonSerializer.Serialize(envelope, JsonOptions);
            lock (gate)
                writer.WriteLine(json);
        }
        catch (Exception ex)
        {
            lock (gate)
                writer.WriteLine(JsonSerializer.Serialize(new
                {
                    seq = System.Threading.Interlocked.Increment(ref sequence),
                    utc = DateTime.UtcNow,
                    category = "capture_error",
                    error = ex.ToString(),
                }, JsonOptions));
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        aggregator.Changed -= OnStateChanged;
        eventLogger.CallbackObserved -= OnCallbackObserved;
        eventLogger.CallPromptObserved -= OnCallPromptObserved;
        eventLogger.BeforeFireCallback -= OnBeforeFireCallback;
        lock (gate)
        {
            writer.Flush();
            writer.Dispose();
        }
    }
}
