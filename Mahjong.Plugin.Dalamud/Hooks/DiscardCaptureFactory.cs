using System;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Hooks.Strategies;
using Mahjong.Plugin.Dalamud.Telemetry;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Hooks;

/// <summary>Always returns AddonPollDiscardCapture — the native-asm sig collides with idle code on post-2026-05 builds. SigscanProbe still records sig drift to telemetry.</summary>
internal static class DiscardCaptureFactory
{
    public static IDiscardCapture Create(
        IPluginLog log,
        IFramework framework,
        ISigScanner sigScanner,
        StateAggregator aggregator,
        AgentEmjEventProbe? agentEvents = null,
        SeatPoolRegistry? seatPools = null,
        ISigprobeLog? sigprobes = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(sigScanner);
        ArgumentNullException.ThrowIfNull(aggregator);
        _ = seatPools;

        SigscanProbe.ProbeDiscardHandler(sigScanner, sigprobes ?? NullSigprobeLog.Instance);

        if (agentEvents is not null)
        {
            var capture = new AgentEventDiscardCapture(log);
            agentEvents.EventObserved += capture.OnAgentEvent;
            aggregator.Changed += capture.OnSnapshotChanged;
            log.Information("[DiscardCapture] using AgentEmj event-triggered strategy with snapshot verification");
            return capture;
        }

        log.Warning("[DiscardCapture] AgentEmj event probe unavailable; using addon-poll fallback");
        var fallback = new AddonPollDiscardCapture(log);
        aggregator.Changed += fallback.OnSnapshotChanged;
        return fallback;
    }
}
