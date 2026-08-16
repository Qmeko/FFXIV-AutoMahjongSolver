using System;
using System.IO;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Logging;

namespace Mahjong.Plugin.Dalamud.Telemetry;

/// <summary>
/// Disabled compatibility stub.
///
/// The previous implementation copied arbitrary native memory from Addon/Agent
/// pointers with Marshal.Copy. An invalid or transient pointer can terminate the
/// process with an AccessViolationException before managed exception handling is
/// able to recover. Full diagnostic capture now relies only on the structured
/// Dalamud/FFXIVClientStructs readers and the plugin's own execution trace.
/// </summary>
public sealed class MemoryDumpRecorder : IDisposable
{
    public const int SchemaVersion = 3;
    internal const int MinAtkValuesForStateChangeDump = int.MaxValue;

    private readonly string memdumpsDir;
    private bool disposed;

    public string MemdumpsDir => memdumpsDir;

    public MemoryDumpRecorder(
        AddonEmjReader reader,
        SeatPoolRegistry seatPools,
        ErrorSink errors,
        string pluginConfigDirectory)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(seatPools);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDirectory);

        memdumpsDir = Path.Combine(pluginConfigDirectory, "memdumps-disabled");
    }

    public void Record(string reason)
    {
        // Intentionally no-op. Do not perform raw native-memory copies here.
        if (disposed)
            return;
    }

    public void Dispose() => disposed = true;
}
