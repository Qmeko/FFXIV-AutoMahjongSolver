using System;
using Dalamud.Plugin.Services;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Hooks.Strategies;

/// <summary>
/// Uses AgentEmj's confirmed UI event as the clock edge, then resolves the
/// authoritative tile from the immediately following table snapshot. Snapshot
/// polling remains only as a safety net when the client omits the event.
/// </summary>
internal sealed class AgentEventDiscardCapture : IDiscardCapture
{
    private readonly IPluginLog log;
    private readonly int[] lastDiscardCounts = new int[4];
    private bool primed;
    private bool pendingAgentCommit;
    private DateTime pendingObservedAtUtc;
    private ulong totalCaptured;
    private int lastTileId = -1;
    private bool disposed;

    public HookHealth Health { get; } = HookHealth.Active;
    public string StrategyName => "agent-event+snapshot";
    public ulong TotalCaptured => totalCaptured;
    public int LastTileId => lastTileId;
    public event Action<DiscardEvent>? DiscardObserved;

    public AgentEventDiscardCapture(IPluginLog log)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        log.Information("[DiscardCapture/agent-event] active — AgentEmj event triggers authoritative snapshot resolution");
    }

    public void OnAgentEvent(AgentEmjObservedEvent evt)
    {
        if (disposed)
            return;

        // Opcode 17 is emitted at the structural hand/river commit boundary in
        // the current Emj client. We deliberately do not trust its positional
        // arguments as tile ids; the next snapshot is authoritative.
        if (evt.Opcode == 17)
        {
            pendingAgentCommit = true;
            pendingObservedAtUtc = evt.ObservedAtUtc;
        }
    }

    public void OnSnapshotChanged(StateSnapshot snap)
    {
        if (disposed)
            return;

        if (!primed)
        {
            for (int i = 0; i < 4 && i < snap.Seats.Count; i++)
                lastDiscardCounts[i] = snap.Seats[i].Discards.Count;
            primed = true;
            pendingAgentCommit = false;
            return;
        }

        DateTime observedAt = pendingAgentCommit ? pendingObservedAtUtc : DateTime.UtcNow;
        bool emitted = false;
        for (int seat = 0; seat < 4 && seat < snap.Seats.Count; seat++)
        {
            var discards = snap.Seats[seat].Discards;
            int previous = lastDiscardCounts[seat];
            int current = discards.Count;
            if (current < previous)
            {
                lastDiscardCounts[seat] = current;
                continue;
            }

            for (int i = previous; i < current; i++)
            {
                Emit(seat, discards[i], observedAt);
                emitted = true;
            }
            lastDiscardCounts[seat] = current;
        }

        if (emitted)
            pendingAgentCommit = false;
        else if (pendingAgentCommit && DateTime.UtcNow - pendingObservedAtUtc > TimeSpan.FromMilliseconds(500))
        {
            log.Warning("[DiscardCapture/agent-event] commit event had no river delta within 500ms; polling fallback retained");
            pendingAgentCommit = false;
        }
    }

    private void Emit(int seat, Tile tile, DateTime observedAt)
    {
        totalCaptured++;
        lastTileId = tile.Id;
        DiscardObserved?.Invoke(new DiscardEvent(seat, tile, observedAt, totalCaptured));
    }

    public void Dispose() => disposed = true;
}
