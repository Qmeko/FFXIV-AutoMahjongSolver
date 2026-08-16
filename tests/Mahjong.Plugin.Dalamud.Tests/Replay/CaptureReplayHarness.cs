using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Mahjong.Plugin.Dalamud.Tests.Stubs;

namespace Mahjong.Plugin.Dalamud.Tests.Replay;

/// <summary>
/// Replays a captured snapshot sequence through the mjai translation layer and
/// reports instruction loss: a visible Pon/Chi/Kan+Pass window for which the
/// pipeline never produced anything Mortal could answer (neither a live
/// decision batch nor a deferred answer keyed to the river-authoritative
/// offer). Also flags protocol-order violations that poison the engine.
/// </summary>
internal static class CaptureReplayHarness
{
    public sealed record Finding(int SnapshotIndex, string Message);

    public static IReadOnlyList<Finding> FindInstructionLoss(IReadOnlyList<StateSnapshot> snapshots)
    {
        var findings = new List<Finding>();
        var tracker = new MjaiSessionTracker();
        string? lastDecisionOfferKey = null;
        bool kyokuStarted = false;
        bool windowOpen = false;
        bool windowSatisfied = false;
        int windowStart = -1;

        for (int i = 0; i < snapshots.Count; i++)
        {
            StateSnapshot state = snapshots[i];
            MjaiEventBatch batch = tracker.BuildBatch(state);

            if (batch.EventCount > 0)
            {
                // Production always writes a produced batch to the engine before
                // reading the response; the sent-dahai record must see it too.
                tracker.NoteBatchSent(batch.Json);
                InspectBatch(batch.Json, i, ref kyokuStarted, findings);
            }

            bool expectsDecision = batch.EventCount > 0
                && ExternalMjaiProcess.BatchExpectsDecision(batch.Json, state.OurSeat);
            if (expectsDecision
                && TryGetLastOpponentDahaiKey(batch.Json, state.OurSeat, out string offerKey))
            {
                lastDecisionOfferKey = offerKey;
            }

            bool livePrompt = ExternalMjaiProcess.IsLiveExternalCallPrompt(state);

            if (livePrompt)
            {
                if (!windowOpen)
                {
                    windowOpen = true;
                    windowSatisfied = false;
                    windowStart = i;
                }

                // A window is answerable when the live batch itself requests a
                // decision, when the answer retained for the last decision
                // offer can be published on this exact surface through the real
                // production matching logic (ExternalMjaiProcess.TryGetDeferredCallChoice),
                // or when the prompt provably has no claimable discard and the
                // production pipeline synthesizes Pass for it.
                windowSatisfied |= expectsDecision
                    || (!windowSatisfied
                        && ExternalMjaiProcess.IsProvablyUnclaimableCallPrompt(state))
                    || (!windowSatisfied
                        && lastDecisionOfferKey is not null
                        && DeferredAnswerIsPublishable(state, lastDecisionOfferKey));

                // Mirror the production CallPromptRepair path: when the batch
                // could not carry the offer, TryChooseCore re-appends the
                // river-authoritative offer unless the engine has already
                // received that exact dahai (sent-key record).
                if (!windowSatisfied
                    && ExternalMjaiProcess.TryGetRiverAuthoritativeCallOffer(
                        state, out Tile repairTile, out int repairActor)
                    && !tracker.AlreadyHasCallOffer(repairActor, repairTile)
                    && tracker.TryAppendAuthoritativeCallPromptBatch(
                        state, repairActor, repairTile,
                        out MjaiEventBatch repairedBatch, out _))
                {
                    tracker.NoteBatchSent(repairedBatch.Json);
                    InspectBatch(repairedBatch.Json, i, ref kyokuStarted, findings);
                    if (TryGetLastOpponentDahaiKey(
                            repairedBatch.Json, state.OurSeat, out string repairedKey))
                    {
                        lastDecisionOfferKey = repairedKey;
                    }
                    windowSatisfied = true;
                }
            }
            else if (windowOpen)
            {
                if (!windowSatisfied)
                {
                    StateSnapshot windowState = snapshots[i - 1];
                    string candidateSummary = string.Join(
                        ",",
                        windowState.Legal.PonCandidates
                            .Concat(windowState.Legal.ChiCandidates)
                            .Concat(windowState.Legal.KanCandidates)
                            .Select(c => $"{c.Kind}:{c.ClaimedTile}@{c.FromSeat}"));
                    findings.Add(new Finding(
                        windowStart,
                        $"call window opened at snapshot {windowStart} and closed at {i} "
                        + "without any answerable offer (instruction loss); "
                        + $"flags={windowState.Legal.Flags} candidates=[{candidateSummary}] "
                        + $"lastDecisionOfferKey={lastDecisionOfferKey ?? "-"}"));
                }
                windowOpen = false;
            }
        }

        if (windowOpen && !windowSatisfied)
        {
            findings.Add(new Finding(
                windowStart,
                $"call window opened at snapshot {windowStart} and never received an answerable offer"));
        }

        return findings;
    }

    private static void InspectBatch(
        string json, int snapshotIndex, ref bool kyokuStarted, List<Finding> findings)
    {
        if (JsonNode.Parse(json) is not JsonArray events)
        {
            findings.Add(new Finding(snapshotIndex, "batch is not a JSON array"));
            return;
        }

        foreach (JsonNode? node in events)
        {
            if (node is not JsonObject evt)
                continue;
            string type = evt["type"]?.GetValue<string>() ?? string.Empty;

            if (type == "start_kyoku")
            {
                kyokuStarted = true;
                continue;
            }
            if (type is "start_game" or "end_kyoku" or "end_game")
                continue;

            if (!kyokuStarted)
            {
                findings.Add(new Finding(
                    snapshotIndex,
                    $"event '{type}' was emitted before any start_kyoku (invalid mjai order)"));
            }

            if (type == "dahai"
                && string.Equals(evt["pai"]?.GetValue<string>(), "?", StringComparison.Ordinal))
            {
                findings.Add(new Finding(
                    snapshotIndex, "dahai with unknown tile '?' is not legal mjai input"));
            }
        }
    }

    /// <summary>
    /// Runs the exact production deferred-call consumption against a live call
    /// surface: a "none" answer retained under <paramref name="offerKey"/> must
    /// be publishable (at minimum as Pass) or the window's instruction is lost.
    /// </summary>
    private static bool DeferredAnswerIsPublishable(StateSnapshot state, string offerKey)
    {
        using var process = new ExternalMjaiProcess(new StubPluginLog(), string.Empty);
        SetPrivateField(process, "deferredCallOfferKey", offerKey);
        SetPrivateField(process, "deferredCallResponse",
            JsonNode.Parse("""{"type":"none"}""")!.AsObject());
        SetPrivateField(process, "latestOpponentDiscardKey", offerKey);
        return process.TryGetDeferredCallChoice(state, out _);
    }

    private static void SetPrivateField(object target, string name, object? value)
    {
        FieldInfo? field = null;
        for (Type? type = target.GetType(); type is not null && field is null; type = type.BaseType)
            field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
            throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static bool TryGetLastOpponentDahaiKey(string json, int ourSeat, out string key)
    {
        key = string.Empty;
        if (JsonNode.Parse(json) is not JsonArray events || events.Count == 0)
            return false;
        if (events[^1] is not JsonObject last)
            return false;

        string type = last["type"]?.GetValue<string>() ?? string.Empty;
        int actor = last["actor"]?.GetValue<int>() ?? -1;
        string pai = last["pai"]?.GetValue<string>() ?? string.Empty;
        if (type is not ("dahai" or "kakan")
            || actor < 0
            || actor == Math.Clamp(ourSeat, 0, 3)
            || string.IsNullOrEmpty(pai)
            || pai == "?")
        {
            return false;
        }

        key = $"{actor}|{pai}";
        return true;
    }
}
