using System;
using Dalamud.Plugin.Services;
using Mahjong.Policy.Efficiency;
using Mahjong.Plugin.Dalamud.ExternalAi;

namespace Mahjong.Plugin.Dalamud.GameState;

public sealed class StateAggregator : IDisposable
{
    private readonly AddonEmjReader reader;
    private readonly IFramework framework;
    private readonly IPolicy? policy;
    private bool disposed;
    private long lastRebuildTicks;
    private int lastContentHash;
    private bool hasContentHash;
    private long lastPendingPolicyRefreshTicks;
    // Keep the last authoritative AI instruction across transient pending/resync
    // refreshes for the exact same live position. This prevents a valid discard or
    // call instruction from disappearing merely because the external engine is
    // prewarming, being recreated, or the EMJ prompt surface is still stabilizing.
    private ActionChoice? retainedAuthoritativeChoice;
    private int retainedDecisionPositionHash;
    private bool hasRetainedDecisionPositionHash;
    private const long MinTickIntervalTicks = 160_000;
    // AI inference runs off the framework thread.  Once it completes, the old
    // 150 ms polling gate kept its already-computed answer off the hint UI for
    // a visibly late frame.  Recheck at the same 16 ms cadence as snapshot
    // rebuilding: this changes no model input or decision, only publishes the
    // cached result on the next framework update.
    private const long PendingPolicyRefreshTicks = MinTickIntervalTicks;

    public StateSnapshot? Latest { get; private set; }

    /// <summary>Scored discards for <see cref="Latest"/>; null off our turn or on scorer throw.</summary>
    public ScoredDiscard[]? LastScored { get; private set; }

    /// <summary>Policy verdict for <see cref="Latest"/>; null when Legal=None or on policy throw.</summary>
    public ActionChoice? LastChoice { get; private set; }

    /// <summary>Scorer exception message, paired with <see cref="LastScored"/>=null.</summary>
    public string? LastScorerError { get; private set; }

    public event Action<StateSnapshot>? Changed;

    public StateAggregator(AddonEmjReader reader, IFramework framework, IPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(framework);
        this.reader = reader;
        this.framework = framework;
        this.policy = policy;

        this.reader.ObservationChanged += OnObservationChanged;
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        framework.Update -= OnFrameworkUpdate;
        reader.ObservationChanged -= OnObservationChanged;
    }

    private void OnObservationChanged(AddonEmjObservation _) => Rebuild();

    /// <summary>Re-evaluates the current legal surface after the selected AI changes.</summary>
    public void RefreshDecision()
    {
        if (Latest is { } snap)
        {
            lastPendingPolicyRefreshTicks = 0;
            RefreshPolicyCache(snap);
            Changed?.Invoke(snap);
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        long now = DateTime.UtcNow.Ticks;
        if (now - lastRebuildTicks < MinTickIntervalTicks)
            return;
        lastRebuildTicks = now;
        Rebuild();
    }

    private void Rebuild()
    {
        // Always call TryBuildSnapshot: it observes MeldTracker and pins ActiveLayout.
        var next = reader.TryBuildSnapshot();
        if (next is null)
        {
            // Addon gone (player left the table) — drop cached state so the UI reverts to the "waiting" empty state.
            if (Latest is not null)
            {
                Latest = null;
                LastScored = null;
                LastChoice = null;
                LastScorerError = null;
                retainedAuthoritativeChoice = null;
                hasRetainedDecisionPositionHash = false;
                hasContentHash = false;
            }
            return;
        }
        if (next.SchemaVersion != StateSnapshot.CurrentSchemaVersion)
            return;

        int hash = ComputeContentHash(next);
        if (hasContentHash && hash == lastContentHash)
        {
            bool mortalPending = LastChoice is { } pending
                && Mahjong.Plugin.Dalamud.ExternalAi.SelectablePolicy.IsPendingChoice(pending);
            long now = DateTime.UtcNow.Ticks;
            if (!mortalPending || now - lastPendingPolicyRefreshTicks < PendingPolicyRefreshTicks)
                return;

            lastPendingPolicyRefreshTicks = now;
            RefreshPolicyCache(next);
            Changed?.Invoke(next);
            return;
        }

        lastContentHash = hash;
        hasContentHash = true;
        Latest = next;
        RefreshPolicyCache(next);
        Changed?.Invoke(next);
    }

    private void RefreshPolicyCache(StateSnapshot snap)
    {
        LastScored = null;
        LastChoice = null;
        LastScorerError = null;

        if (policy is null)
            return;
        if (snap.Legal.Flags == ActionFlags.None)
            return;

        int decisionPositionHash = ComputeDecisionPositionHash(snap);

        if (snap.Legal.Can(ActionFlags.Discard))
        {
            try
            { LastScored = DiscardScorer.Score(snap); }
            catch (Exception ex)
            { LastScorerError = ex.Message; }
        }

        try
        {
            var chosen = policy.Choose(snap);
            bool pending = SelectablePolicy.IsPendingChoice(chosen);

            // State 6 is EMJ's self-declare list. Some refreshes omit the
            // Riichi bit even though the visible list still contains Riichi/Pass.
            // Keep an exact AI Riichi result when its discard tile is still in the
            // current 14-tile hand; reject it everywhere else as stale.
            bool riichiSurface = snap.Legal.Can(ActionFlags.Riichi)
                || (snap.AddonStateCode == 6
                    && snap.Hand.Count % 3 == 2
                    && snap.Legal.Can(ActionFlags.Discard));
            bool validRiichi = chosen.Kind != ActionKind.Riichi
                || (riichiSurface
                    && chosen.DiscardTile is { } riichiTile
                    && snap.Hand.Contains(riichiTile));

            if (!pending && validRiichi && MjaiActionMapper.IsLegal(chosen, snap))
            {
                LastChoice = chosen;
                retainedAuthoritativeChoice = chosen;
                retainedDecisionPositionHash = decisionPositionHash;
                hasRetainedDecisionPositionHash = true;
                return;
            }

            // A pending sentinel is not a new instruction. Do not overwrite a
            // previously completed answer for the same board position. The legal
            // check prevents carrying a discard/call into the next turn or prompt.
            if (pending
                && hasRetainedDecisionPositionHash
                && retainedDecisionPositionHash == decisionPositionHash
                && retainedAuthoritativeChoice is { } retained
                && MjaiActionMapper.IsLegal(retained, snap))
            {
                LastChoice = retained;
                return;
            }

            // No authoritative answer exists yet. Preserve the pending sentinel
            // itself so Rebuild() continues polling the external engine on the
            // 16 ms pending cadence. Clearing LastChoice here made mortalPending
            // false, permanently stopped policy refreshes for an unchanged board,
            // and left the UI at "Waiting for a decision" even after Mortal had
            // completed in the background.
            if (pending)
            {
                LastChoice = chosen;
                return;
            }

            // A completed but unusable answer must not survive this position.
            if (!pending && hasRetainedDecisionPositionHash
                && retainedDecisionPositionHash == decisionPositionHash)
            {
                retainedAuthoritativeChoice = null;
                hasRetainedDecisionPositionHash = false;
            }
        }
        catch
        {
            // A transient policy exception must not erase an already completed
            // instruction for this exact live position.
            if (hasRetainedDecisionPositionHash
                && retainedDecisionPositionHash == decisionPositionHash
                && retainedAuthoritativeChoice is { } retained
                && MjaiActionMapper.IsLegal(retained, snap))
            {
                LastChoice = retained;
            }
        }
    }


    /// <summary>
    /// Hashes the physical decision position while intentionally ignoring transient
    /// EMJ state codes and legal-surface flicker. A new draw/discard/meld/score or
    /// wall movement changes this hash, so an instruction cannot leak into a later
    /// decision.
    /// </summary>
    private static int ComputeDecisionPositionHash(StateSnapshot snap)
    {
        var h = new HashCode();
        h.Add(snap.WallRemaining);
        h.Add(snap.TurnIndex);
        h.Add(snap.OurSeat);
        h.Add(snap.RoundWind);
        h.Add(snap.DealerSeat);
        h.Add(snap.Honba);
        h.Add(snap.RiichiSticks);
        h.Add(snap.OurRiichi);
        h.Add(snap.OurIppatsu);
        foreach (var t in snap.Hand)
            h.Add(t.Id);
        foreach (var m in snap.OurMelds)
        {
            h.Add((int)m.Kind);
            h.Add(m.ClaimedFromSeat);
            foreach (var t in m.Tiles)
                h.Add(t.Id);
        }
        foreach (var t in snap.DoraIndicators)
            h.Add(t.Id);
        foreach (var score in snap.Scores)
            h.Add(score);
        foreach (var seat in snap.Seats)
        {
            h.Add(seat.DiscardCount);
            foreach (var t in seat.Discards)
                h.Add(t.Id);
            foreach (var m in seat.Melds)
            {
                h.Add((int)m.Kind);
                h.Add(m.ClaimedFromSeat);
                foreach (var t in m.Tiles)
                    h.Add(t.Id);
            }
            h.Add(seat.Riichi);
            h.Add(seat.RiichiDiscardIndex);
            h.Add(seat.Ippatsu);
        }
        return h.ToHashCode();
    }

    /// <summary>Content fingerprint; record equality reference-checks list fields and reports false on every fresh snapshot.</summary>
    private static int ComputeContentHash(StateSnapshot snap)
    {
        var h = new HashCode();
        h.Add(snap.WallRemaining);
        h.Add(snap.TurnIndex);
        h.Add((int)snap.Legal.Flags);
        h.Add(snap.Legal.PonCandidates.Count);
        h.Add(snap.Legal.ChiCandidates.Count);
        h.Add(snap.Legal.KanCandidates.Count);
        h.Add(snap.OurRiichi);
        h.Add(snap.OurIppatsu);
        h.Add(snap.OurSeat);
        h.Add(snap.RoundWind);
        h.Add(snap.DealerSeat);
        h.Add(snap.Honba);
        h.Add(snap.RiichiSticks);
        h.Add(snap.AkaDora);
        h.Add(snap.AddonStateCode);
        foreach (var t in snap.Hand)
            h.Add(t.Id);
        foreach (var m in snap.OurMelds)
        {
            h.Add((int)m.Kind);
            foreach (var t in m.Tiles)
                h.Add(t.Id);
        }
        foreach (var t in snap.DoraIndicators)
            h.Add(t.Id);
        foreach (var s in snap.Scores)
            h.Add(s);
        foreach (var s in snap.Seats)
        {
            h.Add(s.DiscardCount);
            foreach (var t in s.Discards)
                h.Add(t.Id);
            foreach (var m in s.Melds)
            {
                h.Add((int)m.Kind);
                foreach (var t in m.Tiles)
                    h.Add(t.Id);
            }
            h.Add(s.Riichi);
            h.Add(s.RiichiDiscardIndex);
            h.Add(s.Ippatsu);
        }
        return h.ToHashCode();
    }
}
