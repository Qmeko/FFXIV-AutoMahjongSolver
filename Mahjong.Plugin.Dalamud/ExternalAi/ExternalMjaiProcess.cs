using System.Diagnostics;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using Mahjong.Engine;

namespace Mahjong.Plugin.Dalamud.ExternalAi;

internal enum ExternalEngineKind
{
    Primary,
    AkochanComparison,
}

/// <summary>
/// Hosts an mjai JSONL bot without ever waiting on the Dalamud framework thread.
/// Model startup and inference are performed by one serialized background task.
/// </summary>
internal sealed class ExternalMjaiProcess : IDisposable
{
    private const string PendingStatusPrefix = "Mortal pending";
    private const int AkochanMinimumHardResponseTimeoutMs = 15000;
    private const int MaximumResponseTimeoutMs = 180000;
    private static readonly TimeSpan FailedCallRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FailedDefaultRetryDelay = TimeSpan.FromSeconds(2);

    // A complete legal synthetic hand. Mortal loads PyTorch + mortal.pth on
    // start_game, then performs one inference on the final tsumo. The same
    // process is kept alive and reset by the real start_game event later.
    private const string WarmupBatchJson =
        "[{\"type\":\"start_game\",\"id\":0,\"names\":[\"Warmup-0\",\"Warmup-1\",\"Warmup-2\",\"Warmup-3\"]}," +
        "{\"type\":\"start_kyoku\",\"bakaze\":\"E\",\"kyoku\":1,\"honba\":0,\"kyotaku\":0,\"oya\":0," +
        "\"scores\":[25000,25000,25000,25000],\"dora_marker\":\"4p\"," +
        "\"tehais\":[[\"1m\",\"2m\",\"3m\",\"4m\",\"5m\",\"6m\",\"2p\",\"3p\",\"4p\",\"6s\",\"7s\",\"8s\",\"E\"]," +
        "[\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\"]," +
        "[\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\"]," +
        "[\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\",\"?\"]]}," +
        "{\"type\":\"tsumo\",\"actor\":0,\"pai\":\"9p\"}]";

    private readonly IPluginLog log;
    private readonly string pluginAssemblyDirectory;
    private readonly ExternalEngineKind engineKind;
    private readonly MjaiSessionTracker tracker = new();
    private readonly object trackerGate = new();
    private readonly object stateGate = new();
    private readonly object processGate = new();

    private readonly string traceDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DomanMahjongSolverDebug",
        "MjaiTrace");
    private readonly ConcurrentQueue<(string FileName, string Line)> pendingTraceWrites = new();
    private int traceWriterActive;

    private Process? process;
    private Task? backgroundTask;
    private string activeIdentity = string.Empty;
    private bool disposed;
    private bool processJustStarted;
    private bool prewarmRequested;
    private string? pendingFingerprint;
    private string? lastFingerprint;
    private string? lastPositionFingerprint;
    private ActionChoice? lastChoice;
    private string? failedFingerprint;
    private DateTime retryFailedFingerprintAfterUtc;
    private string status = "Not started";
    private long lastInferenceMs;
    private int restartCount;
    private long aiTraceSequence;
    // Exact event journal for the current Akochan hand. A native process restart
    // must not force a lossy stateless rebuild from an 11/8/5/2-tile open hand.
    // The journal is replayed into the fresh process before the current decision
    // boundary, while the tracker itself remains on the authoritative live state.
    private readonly List<JsonObject> orderedReplayJournal = [];
    // The engine announced on stderr that its internal game state is broken
    // (libriichi "rule violation" / "bot error"). Every later batch would be
    // answered with empty/none forever (field capture 2026-08-01 18:58: the
    // fifth-8s violation left the overlay on "Calculating…" for minutes), so
    // the next decision request must discard the poisoned session and rebuild
    // from the live snapshot instead of waiting.
    private volatile bool enginePoisoned;
    // Mortal/Akochan can answer Pon/Chi/Kan/Ron/Pass before FFXIV exposes the call UI.
    // Preserve the exact response and map it only when the matching live prompt exists.
    // Without this, the offer is already synchronized, later polls produce events=0,
    // and the 298k answer is lost while the overlay stays on "Calculating…".
    private string? deferredCallOfferKey;
    private JsonObject? deferredCallResponse;
    // Offer key that has already been served to the UI at least once. The
    // response is intentionally NOT cleared on first consumption: the overlay
    // mirrors the newest poll result, so a one-shot answer vanished ~0.3s after
    // the call prompt opened while the window stayed up for seconds (field
    // capture 2026-08-01 22:21:44, a Pass on a chi window disappeared and 12
    // later polls produced events=0/no instruction). Re-serve the latched
    // answer on every poll while the same offer window is open; this field
    // keeps the consumption log to one Information line per window.
    private string? deferredCallServedKey;
    // River length of the offer actor when the response was stored. A later,
    // physically different call window can reuse the same actor|tile key; the
    // actor's river is strictly longer by then, so any growth marks the latched
    // answer as belonging to a closed window and it must not be re-served.
    private int deferredCallOfferDiscardCount = -1;
    // Akochan can answer before EMJ has finished publishing the matching legal
    // surface (most notably reach before the Riichi flag appears). Preserve the
    // raw action against the board position and remap it once the UI catches up.
    private string? deferredDecisionPositionFingerprint;
    private JsonObject? deferredDecisionResponse;
    // Keep the exact Chi/Pon/Kan from the moment its callback is dispatched
    // until FFXIV proves that the mandatory follow-up discard committed. An AI
    // response alone is not completion: the call window can remain visible, the
    // mapped discard can be lost during a process restart, and clearing here used
    // to leave an unrecoverable 11/8/5/2-tile stateless hand.
    private ActionChoice? committedOwnCallAwaitingDecision;
    private StateSnapshot? committedOwnCallRecoveryState;
    private bool committedOwnCallConfirmedByGame;
    private int committedOwnCallPostCallHandCount = -1;
    private int committedOwnCallDiscardCountAtDispatch = -1;
    private int committedOwnCallWallAtDispatch = -1;
    // EMJ can briefly expose stale call labels while the live prompt is still
    // being populated. Require the same authoritative actor+tile offer on two
    // consecutive snapshots before asking Akochan. This blocks one-frame
    // Chi/Pon swaps without adding a fixed sleep to normal decisions.
    private string? observedCallPromptKey;
    private int observedCallPromptSamples;
    // Track the exact opponent discard that opened the current call prompt.
    // Candidate rows reconstructed from the closed hand can contain several
    // technically callable tiles and unreliable FromSeat values; the discard
    // delta is the authoritative actor+tile for the live prompt.
    private readonly int[] observedDiscardCounts = new int[4];
    private readonly HashSet<string> recentOpponentDiscardKeys = new(StringComparer.Ordinal);
    private bool discardObservationInitialized;
    private string? latestOpponentDiscardKey;
    // EMJ can display a Pon/Chi/Kan prompt that no real discard supports (field
    // captures: "MinKan 1p" while no river tip is callable at all). Mortal can
    // never be asked about such a prompt, so after the same unclaimable surface
    // persists briefly (rules out one-frame river reader lag) the only valid
    // decision, Pass, is published directly instead of leaving the overlay on
    // "calculating" until the game times the prompt out.
    private string? unclaimablePromptFingerprint;
    private long unclaimablePromptSinceTimestamp;
    private const int UnclaimablePromptHoldMs = 300;
    // When a Pon/Chi/Kan prompt appears while Mortal is still finishing an older
    // discard decision, queue that urgent prompt immediately after the in-flight
    // work completes instead of waiting for the next poll cycle.
    private StateSnapshot? urgentRequeueState;
    private string? urgentRequeueFingerprint;
    private Configuration? urgentRequeueCfg;
    private ActionChoice urgentRequeueBuiltIn = ActionChoice.Pass();
    private bool urgentRequeuePending;

    public ExternalMjaiProcess(
        IPluginLog log,
        string pluginAssemblyDirectory,
        ExternalEngineKind engineKind = ExternalEngineKind.Primary)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.pluginAssemblyDirectory = pluginAssemblyDirectory ?? string.Empty;
        this.engineKind = engineKind;
    }

    private string EngineName => engineKind == ExternalEngineKind.AkochanComparison ? "Akochan" : "Mortal";

    public string Status
    {
        get
        {
            string value = Volatile.Read(ref status);
            return engineKind == ExternalEngineKind.AkochanComparison
                ? value.Replace("Mortal", "Akochan", StringComparison.Ordinal)
                : value;
        }
    }
    public long LastInferenceMs => Interlocked.Read(ref lastInferenceMs);
    public int RestartCount => Volatile.Read(ref restartCount);

    /// <summary>
    /// Retains an exact open-call choice as soon as the verified callback path
    /// accepted it. This is tentative until <see cref="NotifyCommittedAction"/>
    /// observes the hand/meld transition, but it is enough to recover if the
    /// process restarts in the narrow interval between the callback and that
    /// transition. No mjai call event is emitted from this method.
    /// </summary>
    public void NotifyDispatchedOpenCall(ActionChoice choice, StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(state);
        if (choice.Kind is not (ActionKind.Pon or ActionKind.Chi or ActionKind.MinKan)
            || choice.Call is null)
            return;

        lock (stateGate)
        {
            committedOwnCallAwaitingDecision = choice;
            committedOwnCallRecoveryState = state;
            committedOwnCallConfirmedByGame = false;
            committedOwnCallPostCallHandCount = -1;
            committedOwnCallDiscardCountAtDispatch = GetOwnDiscardCount(state);
            committedOwnCallWallAtDispatch = state.WallRemaining;
        }

        log.Information(
            "[{Engine}CommittedAction] dispatched kind={Kind} hand={Hand} discards={Discards}; awaiting structural commit",
            EngineName, choice.Kind, state.Hand.Count, GetOwnDiscardCount(state));
    }

    /// <summary>
    /// Drops only a tentative open-call record when the dispatch outcome window
    /// closes without any hand or meld transition. A game-confirmed call is never
    /// cancelled here because it still requires the mandatory follow-up discard.
    /// </summary>
    public void CancelDispatchedOpenCall(ActionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        lock (stateGate)
        {
            if (committedOwnCallConfirmedByGame
                || committedOwnCallAwaitingDecision is null
                || committedOwnCallAwaitingDecision.Kind != choice.Kind)
                return;

            ClearCommittedOwnCallState();
        }

        log.Warning(
            "[{Engine}CommittedAction] open-call callback had no structural commit; discarded tentative {Kind} recovery state",
            EngineName, choice.Kind);
    }

    /// <summary>
    /// Publishes an action only after the FFXIV snapshot proves that the UI
    /// committed it. This is required for Pon/Chi because the old call window
    /// can remain visible while the closed hand has already shrunk. Snapshot-
    /// only meld inference can lag or miss that transition, which previously
    /// left Akochan replaying the old call instruction until it timed out.
    /// </summary>
    public void NotifyCommittedAction(ActionChoice choice, StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(state);

        lock (stateGate)
        {
            lastFingerprint = null;
            lastPositionFingerprint = null;
            lastChoice = null;
            ClearDeferredCallLocked();
            deferredDecisionPositionFingerprint = null;
            deferredDecisionResponse = null;
            observedCallPromptKey = null;
            observedCallPromptSamples = 0;
            failedFingerprint = null;
            retryFailedFingerprintAfterUtc = default;
            if (choice.Kind is ActionKind.Pon or ActionKind.Chi or ActionKind.MinKan
                && choice.Call is not null)
            {
                committedOwnCallAwaitingDecision = choice;
                committedOwnCallRecoveryState = state;
                committedOwnCallConfirmedByGame = true;
                committedOwnCallPostCallHandCount = state.Hand.Count;
                if (committedOwnCallDiscardCountAtDispatch < 0)
                    committedOwnCallDiscardCountAtDispatch = GetOwnDiscardCount(state);
                committedOwnCallWallAtDispatch = state.WallRemaining;
            }
        }

        lock (trackerGate)
            tracker.NoteChoice(choice, state);

        SetStatus(choice.PostCallDiscardTile is { } postCallTile
            ? $"{EngineName} connected: committed {choice.Kind}; retained discard {postCallTile}"
            : $"{EngineName} connected: committed {choice.Kind}; awaiting next decision");
        log.Information(
            "[{Engine}CommittedAction] kind={Kind} hand={Hand} melds={Melds} legal={Legal}",
            EngineName, choice.Kind, state.Hand.Count, state.OurMelds.Count, state.Legal.Flags);
    }

    public bool TryGetCommittedOwnCallRecovery(out ActionChoice? choice, out StateSnapshot? state)
    {
        lock (stateGate)
        {
            choice = committedOwnCallAwaitingDecision;
            state = committedOwnCallRecoveryState;
            return choice is not null && state is not null && committedOwnCallConfirmedByGame;
        }
    }

    private void ClearCommittedOwnCallIfFollowUpObserved(StateSnapshot state)
    {
        if (committedOwnCallAwaitingDecision is null
            || !IsCommittedCallFollowUpObserved(
                state,
                committedOwnCallConfirmedByGame,
                committedOwnCallPostCallHandCount,
                committedOwnCallDiscardCountAtDispatch,
                committedOwnCallWallAtDispatch))
            return;

        ActionKind kind = committedOwnCallAwaitingDecision.Kind;
        ClearCommittedOwnCallState();
        log.Information(
            "[{Engine}CommittedAction] mandatory follow-up discard confirmed; released {Kind} recovery state",
            EngineName, kind);
    }

    private void ClearCommittedOwnCallState()
    {
        committedOwnCallAwaitingDecision = null;
        committedOwnCallRecoveryState = null;
        committedOwnCallConfirmedByGame = false;
        committedOwnCallPostCallHandCount = -1;
        committedOwnCallDiscardCountAtDispatch = -1;
        committedOwnCallWallAtDispatch = -1;
    }

    internal static bool IsCommittedCallFollowUpObserved(
        StateSnapshot state,
        bool confirmedByGame,
        int postCallHandCount,
        int discardCountAtDispatch,
        int wallAtDispatch)
    {
        ArgumentNullException.ThrowIfNull(state);
        int currentDiscards = GetOwnDiscardCount(state);
        if (discardCountAtDispatch >= 0 && currentDiscards > discardCountAtDispatch)
            return true;

        if (confirmedByGame
            && postCallHandCount > 0
            && state.Hand.Count > 0
            && state.Hand.Count < postCallHandCount)
            return true;

        // A new hand also invalidates an unconsumed recovery record. Wall count
        // only decreases within a hand, so a material increase plus a closed
        // 13/14-tile shape is an unambiguous hand boundary.
        return wallAtDispatch >= 0
            && state.WallRemaining > wallAtDispatch + 4
            && state.Hand.Count is 13 or 14
            && state.OurMelds.Count == 0;
    }

    private static int GetOwnDiscardCount(StateSnapshot state)
    {
        int seat = Math.Clamp(state.OurSeat, 0, 3);
        if (seat >= state.Seats.Count)
            return 0;
        SeatView view = state.Seats[seat];
        return Math.Max(view.DiscardCount, view.Discards.Count);
    }

    private static bool IsMandatoryPostCallDiscardSurface(StateSnapshot state) =>
        state.Legal.Can(ActionFlags.Discard)
        && state.Hand.Count is 11 or 8 or 5 or 2;

    // The concealed-hand shrink is authoritative even when EMJ is still exposing
    // the previous Pon/Chi/Pass flags. Every caller of SelectablePolicy reaches
    // this shared gate, so neither the logger nor the state aggregator can start a
    // second Akochan request in the frame before AutoPlayLoop confirms the meld.
    internal static bool IsAcceptedOpenCallTransition(
        StateSnapshot state,
        ActionChoice? committedCall) =>
        committedCall is
        {
            Kind: ActionKind.Chi or ActionKind.Pon,
            PostCallDiscardTile: not null,
        }
        && state.Hand.Count is 11 or 8 or 5 or 2;

    internal static bool IsExactPostCallDiscardPending(
        StateSnapshot state,
        ActionChoice? committedCall) =>
        IsAcceptedOpenCallTransition(state, committedCall)
        && IsMandatoryPostCallDiscardSurface(state);

    public bool IsBusy
    {
        get
        {
            lock (stateGate)
                return backgroundTask is { IsCompleted: false };
        }
    }

    public bool TryGetDeferredDecisionChoice(StateSnapshot state, out ActionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (stateGate)
        {
            string? position = deferredDecisionPositionFingerprint;
            JsonObject? response = deferredDecisionResponse;
            if (position is null || response is null
                || !string.Equals(position, PositionFingerprint(state), StringComparison.Ordinal))
            {
                choice = ActionChoice.Pass();
                return false;
            }

            ActionChoice fallback = ActionChoice.Pass("deferred decision fallback");
            if (!MjaiActionMapper.TryMap(
                    response, state, fallback, out choice, EngineName,
                    allowUnreliableCallTarget: engineKind == ExternalEngineKind.AkochanComparison))
                return false;

            deferredDecisionPositionFingerprint = null;
            deferredDecisionResponse = null;
            PublishChoiceForCurrentState(state, choice);
            SetStatus($"{EngineName} connected: {choice.Kind} (deferred surface)");
            log.Information(
                "[{Engine}DeferredDecision] consumed action={Action} position={Position} legal={Legal}",
                EngineName, choice.Kind, position, state.Legal.Flags);
            return true;
        }
    }

    public bool TryGetDeferredCallChoice(StateSnapshot state, out ActionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Legal.Can(ActionFlags.Pass))
        {
            choice = ActionChoice.Pass();
            return false;
        }

        lock (stateGate)
        {
            string? key = deferredCallOfferKey;
            JsonObject? response = deferredCallResponse;
            if (key is null || response is null)
            {
                choice = ActionChoice.Pass();
                return false;
            }

            // A later call window can reuse the same actor|tile key (the same
            // opponent discards the same tile kind again). The actor's river is
            // strictly longer by then, so growth past the recorded length marks
            // this latched answer as belonging to a closed window: drop it and
            // let the fresh batch produce a new answer instead of re-serving a
            // decision made for a different hand.
            if (deferredCallOfferDiscardCount >= 0
                && TryParseOfferActor(key, out int offerActor)
                && SeatRiverLength(state, offerActor) > deferredCallOfferDiscardCount)
            {
                log.Information(
                    "[{Engine}DeferredCall] dropped latched answer for a closed window key={Key} riverLen={RiverLen} storedLen={StoredLen}",
                    EngineName, key, SeatRiverLength(state, offerActor), deferredCallOfferDiscardCount);
                ClearDeferredCallLocked();
                choice = ActionChoice.Pass();
                return false;
            }

            if (!CallOfferMatches(state, key))
            {
                // Mortal already answered "none" for the newest opponent discard.
                // A visible prompt that does not match that offer is then either
                // the same window with garbage candidate rows or a stale
                // re-display of an already-passed window (field captures
                // 2026-07-31: Pon/Kan surfaces flickering back after the next
                // player had already discarded). mjai can only call the newest
                // discard, so Pass is the sole answer the engine can express.
                // The key must still be provably current: its tile has to be the
                // actor's concrete river tip. A stale key (river moved on while
                // the engine was not asked) must not swallow a real call window.
                if (string.Equals(latestOpponentDiscardKey, key, StringComparison.Ordinal)
                    && string.Equals(
                        response["type"]?.GetValue<string>(), "none", StringComparison.Ordinal)
                    && OfferKeyIsRiverTip(state, key)
                    && IsLiveExternalCallPrompt(state))
                {
                    choice = ActionChoice.Pass(
                        $"{EngineName}: 最新の捨て牌は鳴き無しと判断済みのため見送り");
                    PublishChoiceForCurrentState(state, choice);
                    SetStatus($"{EngineName} connected: Pass (stale call surface)");
                    // Keep the latched answer alive: the overlay mirrors the
                    // newest poll, so clearing here made this Pass vanish on
                    // the next poll while the window stayed open.
                    if (!string.Equals(deferredCallServedKey, key, StringComparison.Ordinal))
                    {
                        deferredCallServedKey = key;
                        log.Information(
                            "[{Engine}DeferredCall] passed stale surface key={Key} legal={Legal}",
                            EngineName, key, state.Legal.Flags);
                    }
                    return true;
                }

                choice = ActionChoice.Pass();
                return false;
            }

            // Map only against the live FFXIV call surface. This is the point at
            // which Pon/Chi/Kan candidates and the Pass button are authoritative.
            // When candidate rows name a tile the river contradicts, they cannot
            // be used to reject the answer either: the offer key was already
            // validated against the public river above, so reconstruct the call
            // from Mortal's exact response instead of dropping the decision.
            bool riverConfirmsOffer = RiverConfirmsOfferKey(state, key);
            ActionChoice fallback = ActionChoice.Pass("deferred call fallback");
            if (!MjaiActionMapper.TryMap(
                    response, state, fallback, out choice, EngineName,
                    allowUnreliableCallTarget: engineKind == ExternalEngineKind.AkochanComparison
                        || riverConfirmsOffer))
                return false;

            PublishChoiceForCurrentState(state, choice);
            SetStatus($"{EngineName} connected: {choice.Kind} (deferred)");
            // The answer stays latched (see deferredCallServedKey): the call
            // window outlives the first consumption by seconds, and every later
            // poll must keep re-serving this decision until the window closes
            // (our discard surface or a superseding opponent discard clears it).
            if (!string.Equals(deferredCallServedKey, key, StringComparison.Ordinal))
            {
                deferredCallServedKey = key;
                log.Information(
                    "[{Engine}DeferredCall] consumed action={Action} key={Key} legal={Legal}",
                    EngineName, choice.Kind, key, state.Legal.Flags);
            }
            return true;
        }
    }

    /// <summary>Drops the latched call answer. Callers must hold <see cref="stateGate"/>.</summary>
    private void ClearDeferredCallLocked()
    {
        deferredCallOfferKey = null;
        deferredCallResponse = null;
        deferredCallServedKey = null;
        deferredCallOfferDiscardCount = -1;
    }

    private static bool TryParseOfferActor(string key, out int actor)
    {
        actor = -1;
        int separator = key.IndexOf('|');
        return separator > 0
            && int.TryParse(key.AsSpan(0, separator), out actor)
            && actor is >= 0 and <= 3;
    }

    private static int SeatRiverLength(StateSnapshot state, int actor) =>
        actor >= 0 && actor < state.Seats.Count
            ? Math.Max(state.Seats[actor].DiscardCount, state.Seats[actor].Discards.Count)
            : -1;

    private void PublishChoiceForCurrentState(StateSnapshot state, ActionChoice choice)
    {
        lastFingerprint = Fingerprint(state);
        lastPositionFingerprint = PositionFingerprint(state);
        lastChoice = choice;
        failedFingerprint = null;
        retryFailedFingerprintAfterUtc = default;
    }

    private bool CallOfferMatches(StateSnapshot state, string key)
    {
        int separator = key.IndexOf('|');
        if (separator <= 0 || separator >= key.Length - 1)
            return false;

        var candidates = state.Legal.PonCandidates
            .Concat(state.Legal.ChiCandidates)
            .Concat(state.Legal.KanCandidates)
            .ToArray();

        if (candidates.Any(candidate =>
            {
                if (candidate.FromSeat < 0 || candidate.ClaimedTile.Id >= Tile.Count34)
                    return false;
                int absoluteActor = (Math.Clamp(state.OurSeat, 0, 3)
                    + Math.Clamp(candidate.FromSeat, 0, 3)) & 3;
                return $"{absoluteActor}|{MjaiJson.EncodeTile(candidate.ClaimedTile)}" == key;
            }))
            return true;

        // Candidate FromSeat is not authoritative on EMJ call prompts. When the
        // exact discard delta that opened this prompt is known, accept that
        // actor+tile key as long as the live candidate surface contains the same
        // claimed tile. This preserves actor safety without trusting stale rows.
        if (string.Equals(latestOpponentDiscardKey, key, StringComparison.Ordinal))
        {
            string encodedTile = key[(key.IndexOf('|') + 1)..];
            if (candidates.Any(candidate =>
                    candidate.ClaimedTile.Id < Tile.Count34
                    && string.Equals(MjaiJson.EncodeTile(candidate.ClaimedTile), encodedTile, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        // The public river is authoritative over candidate rows. EMJ can expose
        // a candidate tile that was never discarded (field capture 2026-08-01:
        // "Pon 6s" rows while the river showed 8m); such rows must not veto an
        // offer key that the river itself confirms: the key's actor really has
        // that tile as its newest discard and the tile is callable right now.
        if (RiverConfirmsOfferKey(state, key))
            return true;

        // Pass-only/Ron-only prompts have no meld candidate, so the visible Pass
        // surface remains the only available match signal for those prompts.
        return candidates.Length == 0 && state.Legal.Can(ActionFlags.Pass);
    }

    /// <summary>
    /// True when the "actor|tile" key still names that actor's concrete newest
    /// river tile, regardless of callability. Used to prove a retained offer
    /// key has not been superseded by a newer discard.
    /// </summary>
    internal static bool OfferKeyIsRiverTip(StateSnapshot state, string key)
    {
        int separator = key.IndexOf('|');
        if (separator <= 0 || separator >= key.Length - 1)
            return false;
        if (!int.TryParse(key[..separator], out int actor))
            return false;
        if (!MjaiJson.TryParseTile(key[(separator + 1)..], out Tile tile))
            return false;
        if (actor < 0 || actor >= state.Seats.Count)
            return false;

        SeatView seat = state.Seats[actor];
        return seat.Discards.Count > 0 && seat.Discards[^1] == tile;
    }

    /// <summary>
    /// Verifies an "actor|tile" offer key directly against the public river:
    /// the actor's newest discard must be exactly that tile and the tile must be
    /// callable with the current hand and legal flags. Unlike
    /// <see cref="TryGetRiverAuthoritativeCallOffer"/> this does not require the
    /// offer to be globally unique — the key already identifies actor and tile.
    /// </summary>
    internal static bool RiverConfirmsOfferKey(StateSnapshot state, string key)
    {
        int separator = key.IndexOf('|');
        if (separator <= 0 || separator >= key.Length - 1)
            return false;
        if (!int.TryParse(key[..separator], out int actor))
            return false;
        if (!MjaiJson.TryParseTile(key[(separator + 1)..], out Tile tile))
            return false;

        int ourSeat = Math.Clamp(state.OurSeat, 0, 3);
        if (actor < 0 || actor >= state.Seats.Count || actor == ourSeat)
            return false;

        SeatView seat = state.Seats[actor];
        if (seat.Discards.Count == 0 || seat.Discards[^1] != tile)
            return false;

        return CanCallTile(state, tile, actor);
    }

    /// <summary>
    /// Publishes Pass for a call prompt that provably no discard supports.
    /// The surface must stay unclaimable for <see cref="UnclaimablePromptHoldMs"/>
    /// on the same board fingerprint first: the public river reader can lag the
    /// prompt by a frame, and passing instantly there would discard a real
    /// Pon/Chi answer. Must be called under <c>stateGate</c>.
    /// </summary>
    private bool TryGetUnclaimablePromptPass(
        StateSnapshot state, string fingerprint, out ActionChoice choice)
    {
        choice = ActionChoice.Pass();
        if (!IsProvablyUnclaimableCallPrompt(state))
        {
            unclaimablePromptFingerprint = null;
            return false;
        }

        if (!string.Equals(unclaimablePromptFingerprint, fingerprint, StringComparison.Ordinal))
        {
            unclaimablePromptFingerprint = fingerprint;
            unclaimablePromptSinceTimestamp = Stopwatch.GetTimestamp();
            return false;
        }

        if (Stopwatch.GetElapsedTime(unclaimablePromptSinceTimestamp).TotalMilliseconds
            < UnclaimablePromptHoldMs)
        {
            return false;
        }

        choice = ActionChoice.Pass($"{EngineName}: 河に鳴ける牌が無い表示のため見送り");
        PublishChoiceForCurrentState(state, choice);
        SetStatus($"{EngineName} connected: Pass (unclaimable prompt)");
        log.Information(
            "[{Engine}UnclaimablePrompt] passed legal={Legal} hand={Hand}",
            EngineName, state.Legal.Flags, state.Hand.Count);
        return true;
    }

    private void ObserveOpponentDiscard(StateSnapshot state)
    {
        int seatCount = Math.Min(4, state.Seats.Count);
        int ourSeat = Math.Clamp(state.OurSeat, 0, 3);

        if (!discardObservationInitialized)
        {
            for (int actor = 0; actor < seatCount; actor++)
            {
                SeatView seat = state.Seats[actor];
                observedDiscardCounts[actor] = Math.Max(seat.DiscardCount, seat.Discards.Count);
            }
            discardObservationInitialized = true;

            // The plugin can begin observing after EMJ has already opened a call
            // prompt. Initializing only the discard counts loses the discard that
            // actually opened the visible Pass/Pon/Chi/Kan surface, leaving Akochan
            // permanently pending. Seed the offer from the authoritative public
            // river snapshot when the currently visible legal call proves which
            // opponent discard is callable.
            SeedVisibleCallOfferFromRiver(state, seatCount, ourSeat);
            return;
        }

        bool reset = false;
        for (int actor = 0; actor < seatCount; actor++)
        {
            SeatView seat = state.Seats[actor];
            int count = Math.Max(seat.DiscardCount, seat.Discards.Count);
            if (count < observedDiscardCounts[actor])
                reset = true;
        }

        if (reset)
        {
            Array.Clear(observedDiscardCounts);
            recentOpponentDiscardKeys.Clear();
            latestOpponentDiscardKey = null;
        }

        // Our new discard starts a new opponent-response window. Discards seen
        // after this point are the only possible sources of the next live call
        // prompt.
        if (ourSeat < seatCount)
        {
            SeatView us = state.Seats[ourSeat];
            int ourCount = Math.Max(us.DiscardCount, us.Discards.Count);
            if (ourCount > observedDiscardCounts[ourSeat])
            {
                recentOpponentDiscardKeys.Clear();
                latestOpponentDiscardKey = null;
            }
        }

        for (int actor = 0; actor < seatCount; actor++)
        {
            SeatView seat = state.Seats[actor];
            int count = Math.Max(seat.DiscardCount, seat.Discards.Count);
            if (actor != ourSeat && count > observedDiscardCounts[actor] && seat.Discards.Count > 0)
            {
                Tile tile = seat.Discards[^1];
                if (tile.Id < Tile.Count34)
                {
                    string key = $"{actor}|{MjaiJson.EncodeTile(tile)}";
                    recentOpponentDiscardKeys.Add(key);
                    latestOpponentDiscardKey = key;
                    log.Debug(
                        "[AkochanCallOffer] observed discard actor={Actor} tile={Tile} count={Count}",
                        actor, MjaiJson.EncodeTile(tile), count);
                }
            }
            observedDiscardCounts[actor] = count;
        }
    }

    private void SeedVisibleCallOfferFromRiver(StateSnapshot state, int seatCount, int ourSeat)
    {
        if (!IsLiveExternalCallPrompt(state))
            return;

        var matches = new List<string>();
        bool chiOnly = state.Legal.Can(ActionFlags.Chi)
            && !state.Legal.Can(ActionFlags.Pon)
            && !state.Legal.Can(ActionFlags.MinKan);
        int chiActor = (ourSeat + 3) & 3;

        for (int actor = 0; actor < seatCount; actor++)
        {
            if (actor == ourSeat || (chiOnly && actor != chiActor))
                continue;

            SeatView seat = state.Seats[actor];
            if (seat.Discards.Count == 0)
                continue;

            Tile tile = seat.Discards[^1];
            if (tile.Id >= Tile.Count34 || !CanCallTile(state, tile, actor))
                continue;

            matches.Add($"{actor}|{MjaiJson.EncodeTile(tile)}");
        }

        string[] unique = matches.Distinct(StringComparer.Ordinal).ToArray();
        if (unique.Length != 1)
            return;

        latestOpponentDiscardKey = unique[0];
        recentOpponentDiscardKeys.Add(unique[0]);
        log.Information(
            "[AkochanCallOffer] seeded visible prompt from river key={Key} legal={Legal}",
            unique[0], state.Legal.Flags);
    }

    public bool TryGetCachedChoice(StateSnapshot state, out ActionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (stateGate)
        {
            string fingerprint = Fingerprint(state);
            string positionFingerprint = PositionFingerprint(state);
            if ((fingerprint == lastFingerprint || positionFingerprint == lastPositionFingerprint)
                && lastChoice is { } cached
                && MjaiActionMapper.IsLegal(cached, state))
            {
                choice = cached;
                return true;
            }
        }

        choice = ActionChoice.Pass();
        return false;
    }

    /// <summary>
    /// Returns the last published choice for the current board position while a
    /// background inference is still running. Hint mode uses this to avoid
    /// flashing Pass between polls; autoplay still treats the pending prefix as
    /// non-actionable.
    /// </summary>
    public bool TryGetPendingRetainedChoice(StateSnapshot state, out ActionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (stateGate)
        {
            if (backgroundTask is { IsCompleted: false }
                && lastChoice is { Kind: not ActionKind.Pass } retained
                && MjaiActionMapper.IsLegal(retained, state))
            {
                string fingerprint = Fingerprint(state);
                string positionFingerprint = PositionFingerprint(state);
                // Matching only on pendingFingerprint used to republish a stale
                // Discard while a fresh Chi/Pon/Pass job was queued for the same
                // poll loop (field log: Discard 6z on Pon/Pass).
                if (string.Equals(lastFingerprint, fingerprint, StringComparison.Ordinal)
                    || string.Equals(lastPositionFingerprint, positionFingerprint, StringComparison.Ordinal)
                    || string.Equals(deferredDecisionPositionFingerprint, positionFingerprint, StringComparison.Ordinal))
                {
                    choice = retained;
                    return true;
                }
            }
        }

        choice = ActionChoice.Pass();
        return false;
    }

    public static bool IsPendingStatus(string? value) =>
        value is not null && value.StartsWith(PendingStatusPrefix, StringComparison.Ordinal);

    /// <summary>Starts Python, loads the model and runs one inference off the game thread.</summary>
    public void BeginPrewarm(Configuration cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (engineKind == ExternalEngineKind.AkochanComparison)
            return;
        if (cfg.AiProvider != AiProvider.BundledMortal)
            return;

        lock (stateGate)
        {
            if (disposed || prewarmRequested || backgroundTask is { IsCompleted: false })
                return;

            prewarmRequested = true;
            SetStatus("Mortal pending: loading model in background");
            backgroundTask = Task.Run(() => PrewarmCore(cfg));
        }
    }

    /// <summary>
    /// Returns a cached completed decision when available. Otherwise queues the
    /// work and returns immediately; it never starts a process or waits for JSONL
    /// on the caller's thread.
    /// </summary>
    public bool TryChoose(
        Configuration cfg,
        StateSnapshot state,
        Func<ActionChoice> builtInFactory,
        out ActionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(builtInFactory);
        choice = ActionChoice.Pass();
        string fingerprint = Fingerprint(state);
        string positionFingerprint = PositionFingerprint(state);

        lock (stateGate)
        {
            ObserveOpponentDiscard(state);
            ClearCommittedOwnCallIfFollowUpObserved(state);

            // Akochan returns Chi/Pon and the mandatory follow-up discard as one
            // atomic response. The hand can shrink before EMJ replaces the stale
            // Pon/Chi/Pass flags with Discard. Treat that hand shape as the shared
            // transaction boundary before consulting the cache or starting any
            // new request; otherwise another caller can publish the accepted meld
            // a second time while AutoPlayLoop is still confirming it.
            if (engineKind == ExternalEngineKind.AkochanComparison
                && IsAcceptedOpenCallTransition(
                    state,
                    committedOwnCallAwaitingDecision))
            {
                Tile tile = committedOwnCallAwaitingDecision!.PostCallDiscardTile!.Value;
                SetStatus(state.Legal.Can(ActionFlags.Discard)
                    ? $"Akochan pending: committing mandatory post-call discard {tile}"
                    : "Akochan pending: waiting for accepted call to commit");
                return false;
            }

            if ((fingerprint == lastFingerprint || positionFingerprint == lastPositionFingerprint)
                && lastChoice is { } cached
                && MjaiActionMapper.IsLegal(cached, state))
            {
                choice = cached;
                SetStatus($"{EngineName} connected: {choice.Kind} (cached)");
                return true;
            }

            if (TryGetUnclaimablePromptPass(state, fingerprint, out choice))
                return true;

            // Mortal and Akochan both consume the mjai event before the EMJ legal
            // surface is ready. Re-entering ChooseCore while a deferred response is
            // waiting re-appends the same opponent dahai and can raise
            // "attempt to witness the fifth …".
            if (deferredDecisionResponse is not null
                && string.Equals(
                    deferredDecisionPositionFingerprint,
                    positionFingerprint,
                    StringComparison.Ordinal))
            {
                SetStatus($"{EngineName} pending: waiting for matching legal surface");
                return false;
            }

            if (disposed)
            {
                SetStatus("External AI is disposed");
                return false;
            }

            if (engineKind == ExternalEngineKind.AkochanComparison)
            {
                if (IsLiveExternalCallPrompt(state))
                {
                    if (!TryGetUniqueLiveCallOfferKey(state, out string liveCallKey))
                    {
                        observedCallPromptKey = null;
                        observedCallPromptSamples = 0;
                        SetStatus("Akochan pending: waiting for an unambiguous call offer");
                        return false;
                    }

                    if (!string.Equals(observedCallPromptKey, liveCallKey, StringComparison.Ordinal))
                    {
                        observedCallPromptKey = liveCallKey;
                        observedCallPromptSamples = 1;
                        SetStatus("Akochan pending: stabilizing call prompt");
                        return false;
                    }

                    observedCallPromptSamples++;
                    if (observedCallPromptSamples < 2)
                    {
                        SetStatus("Akochan pending: stabilizing call prompt");
                        return false;
                    }
                }
                else
                {
                    observedCallPromptKey = null;
                    observedCallPromptSamples = 0;
                }
            }

            // After a timeout/crash/invalid response, let SelectablePolicy use
            // its configured fallback for a short period instead of endlessly
            // returning a pending sentinel and retrying 60 times per second.
            if (fingerprint == failedFingerprint && DateTime.UtcNow < retryFailedFingerprintAfterUtc)
                return false;

            if (!cfg.MortalAutoRestart && failedFingerprint is not null && process is null)
            {
                SetStatus("Mortal stopped; auto restart disabled");
                return false;
            }

            if (backgroundTask is { IsCompleted: false })
            {
                if (IsUrgentExternalPrompt(state)
                    && !string.Equals(pendingFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    urgentRequeueState = state;
                    urgentRequeueFingerprint = fingerprint;
                    urgentRequeueCfg = cfg;
                    urgentRequeueBuiltIn = builtInFactory();
                    urgentRequeuePending = true;
                }

                SetStatus(pendingFingerprint == fingerprint
                    ? "Mortal pending: calculating this decision"
                    : "Mortal pending: finishing background work");
                return false;
            }

            // Build the fallback only once, and only when a fresh external-AI request
            // is actually queued. Previously SelectablePolicy evaluated the built-in
            // policy on every 16 ms pending poll, competing with Mortal/Akochan and
            // delaying both the game thread and the external process.
            ActionChoice builtIn = builtInFactory();
            pendingFingerprint = fingerprint;
            SetStatus("Mortal pending: decision queued");

            // The external process is persistent; creating a brand-new dedicated OS
            // thread for every decision only adds scheduling/startup overhead. The
            // serialized processGate already guarantees one in-flight request, so a
            // normal thread-pool work item is sufficient and publishes the answer
            // sooner under repeated turn/call decisions.
            backgroundTask = Task.Run(() => ChooseCore(cfg, state, builtIn, fingerprint));
            return false;
        }
    }

    private void PrewarmCore(Configuration cfg)
    {
        try
        {
            if (!TryBuildLaunchSpec(cfg, out var launch, out string launchError))
                throw new InvalidOperationException(launchError);

            lock (processGate)
            {
                if (disposed)
                    return;

                EnsureStartedCore(launch, playerId: 0);
                if (process is null)
                    throw new InvalidOperationException("Mortal process did not start");

                process.StandardInput.WriteLine(WarmupBatchJson);
                process.StandardInput.Flush();

                string? response = ReadLineWithTimeout(process, cfg.ExternalAiStartupTimeoutMs);
                if (response is null)
                    throw new TimeoutException($"Mortal warmup produced no response within {cfg.ExternalAiStartupTimeoutMs} ms");

                JsonObject? obj = MjaiJson.ParseObject(response);
                string? type = obj?["type"]?.GetValue<string>();
                string? tile = obj?["pai"]?.GetValue<string>();
                if (obj is null || type is not ("dahai" or "reach")
                    || (type == "dahai" && string.IsNullOrWhiteSpace(tile)))
                    throw new InvalidDataException($"Mortal warmup returned an invalid action: {Truncate(response, 180)}");

                // The real tracker will emit a fresh start_game/start_kyoku batch.
                // bot.py supports start_game at any time and resets its PlayerState,
                // while the expensive imported model remains resident.
                lock (trackerGate)
                    tracker.Reset();
                processJustStarted = false;
            }

            SetStatus("Mortal ready (prewarmed)");
            log.Information("[ExternalAI] Mortal model prewarmed in the background");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or InvalidDataException or AggregateException)
        {
            SetStatus($"Mortal prewarm failed: {ex.Message}");
            log.Warning(ex, "[ExternalAI] Mortal background prewarm failed");
            lock (processGate)
                StopProcessCore();
        }
        finally
        {
            lock (stateGate)
            {
                pendingFingerprint = null;
                backgroundTask = null;
            }
        }
    }

    private void ChooseCore(Configuration cfg, StateSnapshot state, ActionChoice builtIn, string fingerprint)
    {
        bool success = false;
        ActionChoice result = builtIn;
        long queuedAt = Stopwatch.GetTimestamp();

        try
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            catch
            {
                // Thread-priority elevation is best-effort only.
            }

            var sw = Stopwatch.StartNew();
            long lockWaitStart = Stopwatch.GetTimestamp();
            lock (processGate)
            {
                long lockAcquired = Stopwatch.GetTimestamp();
                if (disposed)
                    return;

                success = TryChooseCore(cfg, state, builtIn, out result);
                long completed = Stopwatch.GetTimestamp();
                log.Information(
                    "[AILatency] engine={Engine} queueMs={QueueMs:F1} processLockMs={LockMs:F1} coreMs={CoreMs:F1} success={Success} action={Action}",
                    EngineName,
                    Stopwatch.GetElapsedTime(queuedAt, lockWaitStart).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(lockWaitStart, lockAcquired).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(lockAcquired, completed).TotalMilliseconds,
                    success,
                    success ? result.Kind.ToString() : "-");
            }
            sw.Stop();
            Interlocked.Exchange(ref lastInferenceMs, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            log.Warning(ex, "[ExternalAI] unexpected background decision failure");
            lock (processGate)
                StopProcessCore();
        }
        finally
        {
            StateSnapshot? requeueState = null;
            string? requeueFingerprint = null;
            Configuration? requeueCfg = null;
            ActionChoice requeueBuiltIn = ActionChoice.Pass();

            lock (stateGate)
            {
                if (success && !disposed)
                {
                    lastFingerprint = fingerprint;
                    lastPositionFingerprint = PositionFingerprint(state);
                    lastChoice = result;
                    failedFingerprint = null;
                    retryFailedFingerprintAfterUtc = default;
                }
                else if (!disposed)
                {
                    failedFingerprint = fingerprint;
                    retryFailedFingerprintAfterUtc = DateTime.UtcNow.Add(
                        IsUrgentExternalPrompt(state) ? FailedCallRetryDelay : FailedDefaultRetryDelay);
                }

                if (urgentRequeuePending
                    && urgentRequeueState is { } queuedState
                    && urgentRequeueFingerprint is { } queuedFingerprint
                    && urgentRequeueCfg is { } queuedCfg
                    && !string.Equals(queuedFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    requeueState = queuedState;
                    requeueFingerprint = queuedFingerprint;
                    requeueCfg = queuedCfg;
                    requeueBuiltIn = urgentRequeueBuiltIn;
                }

                urgentRequeueState = null;
                urgentRequeueFingerprint = null;
                urgentRequeueCfg = null;
                urgentRequeuePending = false;
                pendingFingerprint = null;
                backgroundTask = null;
            }

            if (requeueState is not null
                && requeueFingerprint is not null
                && requeueCfg is not null
                && !disposed)
            {
                lock (stateGate)
                {
                    pendingFingerprint = requeueFingerprint;
                    SetStatus("Mortal pending: urgent call queued");
                    backgroundTask = Task.Run(() => ChooseCore(requeueCfg!, requeueState!, requeueBuiltIn, requeueFingerprint!));
                }
            }
        }
    }

    private bool TryChooseCore(Configuration cfg, StateSnapshot state, ActionChoice builtIn, out ActionChoice choice)
    {
        choice = builtIn;
        long requestId = Interlocked.Increment(ref aiTraceSequence);
        long traceStart = Stopwatch.GetTimestamp();
        long launchReady = traceStart;
        long processReady = traceStart;
        long batchReady = traceStart;
        long writeBegin = traceStart;
        long writeEnd = traceStart;
        long responseReady = traceStart;
        long parseReady = traceStart;
        long decisionReady = traceStart;
        int sendBytes = 0;
        int receiveBytes = 0;
        int eventCount = 0;
        bool expectsDecision = false;
        bool softTimeoutExceeded = false;

        log.Information(
            "[AITrace] id={RequestId} stage=request_begin engine={Engine} legal={Legal} hand={HandCount} wall={Wall}",
            requestId, EngineName, state.Legal.Flags, state.Hand.Count, state.WallRemaining);

        if (state.Legal.Can(ActionFlags.Discard)
            && state.Hand.Count > 0
            && state.Hand.Count % 3 == 2)
        {
            // Our draw or mandatory post-call discard supersedes every older
            // opponent call offer. Keeping a stale none/pon/chi response here
            // makes the UI show Pass while FFXIV is waiting for our discard.
            lock (stateGate)
            {
                ClearDeferredCallLocked();
                if (engineKind == ExternalEngineKind.AkochanComparison)
                {
                    observedCallPromptKey = null;
                    observedCallPromptSamples = 0;
                }
            }
        }

        if (!TryBuildLaunchSpec(cfg, out var launch, out string launchError))
        {
            SetStatus(launchError);
            return false;
        }

        try
        {
            // A poisoned engine never answers again (it rejects every event
            // after an internal rule violation). Kill it together with the
            // replay journal that reproduces the poison, and let the tracker
            // bootstrap a fresh round from the authoritative live snapshot.
            if (enginePoisoned)
            {
                enginePoisoned = false;
                log.Warning(
                    "[EngineWatchdog] engine={Engine} restarting after a broken session; resynchronizing from the live snapshot",
                    EngineName);
                StopProcessCore(preserveSession: false, preserveCommittedOwnCall: true);
            }

            launchReady = Stopwatch.GetTimestamp();
            bool canReplayOrderedSession = orderedReplayJournal.Count > 0;
            EnsureStartedCore(launch, state.OurSeat, preserveSession: canReplayOrderedSession);
            processReady = Stopwatch.GetTimestamp();
            if (process is null)
                return false;

            // A confirmed Chi/Pon/Kan is already published to the live tracker by
            // NotifyCommittedAction. Re-publishing it on every transient post-call
            // snapshot sends the same call multiple times to Akochan and corrupts
            // its ordered hand state. Replay the exact call only after a fresh
            // native process has reset the tracker; the first recovery batch then
            // consumes it once.
            ActionChoice? committedCallRecovery;
            bool committedCallCanReplay;
            lock (stateGate)
            {
                committedCallRecovery = committedOwnCallAwaitingDecision;
                committedCallCanReplay = committedOwnCallConfirmedByGame
                    || IsMandatoryPostCallDiscardSurface(state);
            }
            if (ShouldReplayCommittedCallAfterProcessStart(
                    processJustStarted,
                    committedCallRecovery,
                    committedCallCanReplay))
            {
                lock (trackerGate)
                    tracker.NoteChoice(committedCallRecovery!, state);
                log.Information(
                    "[{Engine}CommittedAction] replayed {Kind} once after native process start",
                    EngineName,
                    committedCallRecovery!.Kind);
            }

            MjaiEventBatch batch;
            lock (trackerGate)
                batch = tracker.BuildBatch(state);

            // EMJ can expose the call popup before its public river snapshot has
            // published the final offered tile. The previous implementation threw
            // away the ordered event batch and manufactured a new start_kyoku from
            // the current 13-tile hand. That erased the real river/meld/riichi
            // context and made external call choices unreliable. Keep the original
            // batch and repair only the offered tile on its final opponent event.
            // Mortal uses the same repair path; an empty batch on a live call prompt
            // otherwise leaves the engine at "waiting for a new event" forever.
            if (IsLiveExternalCallPrompt(state))
            {
                if (!TryGetUniqueLiveCallOfferKey(state, out string liveCallKey))
                {
                    string pendingPrefix = engineKind == ExternalEngineKind.AkochanComparison
                        ? "Akochan pending"
                        : PendingStatusPrefix;
                    SetStatus($"{pendingPrefix}: waiting for an unambiguous call offer");
                    return false;
                }

                bool hasIncrementalOffer =
                    TryGetLastOpponentCallOfferKey(batch.Json, state.OurSeat, out string incrementalOfferKey);
                bool incrementalMatchesPrompt =
                    hasIncrementalOffer && CallOfferMatches(state, incrementalOfferKey);

                bool alreadySyncedOffer = false;
                if (!incrementalMatchesPrompt
                    && TryParseCallOfferKey(liveCallKey, out int syncedActor, out Tile syncedTile))
                {
                    lock (trackerGate)
                        alreadySyncedOffer = tracker.AlreadyHasCallOffer(syncedActor, syncedTile);
                    if (alreadySyncedOffer)
                    {
                        log.Information(
                            "[CallPromptRepair] engine={Engine} offer={Offer} already synchronized; skipping re-append legal={Legal}",
                            EngineName, liveCallKey, state.Legal.Flags);
                    }
                }

                if (!incrementalMatchesPrompt && !alreadySyncedOffer)
                {
                    bool corrected = false;
                    MjaiEventBatch correctedBatch = MjaiEventBatch.Empty("uninitialized call repair");
                    string sourceOfferKey = string.Empty;
                    string correctedOfferKey = string.Empty;
                    if (TryParseCallOfferKey(liveCallKey, out int liveActor, out Tile liveTile))
                    {
                        lock (trackerGate)
                        {
                            corrected = tracker.TryCorrectAuthoritativeCallPromptBatch(
                                state,
                                batch,
                                liveActor,
                                liveTile,
                                out correctedBatch,
                                out sourceOfferKey,
                                out correctedOfferKey);
                        }

                        if (!corrected)
                        {
                            lock (trackerGate)
                            {
                                corrected = tracker.TryAppendAuthoritativeCallPromptBatch(
                                    state,
                                    liveActor,
                                    liveTile,
                                    out correctedBatch,
                                    out correctedOfferKey);
                            }
                            if (corrected)
                                sourceOfferKey = "(append-fallback)";
                        }
                    }

                    if (!corrected)
                    {
                        // River conflict / stale ClaimedTile will not be fixed by
                        // urgent re-append of the same invented offer.
                        bool riverConflict = TryParseCallOfferKey(liveCallKey, out int conflictActor, out Tile conflictTile)
                            && MjaiSessionTracker.CallOfferConflictsWithRiver(state, conflictActor, conflictTile);
                        if (riverConflict)
                        {
                            SetStatus($"{EngineName} pending: waiting for an unambiguous call offer");
                            log.Warning(
                                "[CallPromptRepair] engine={Engine} rejected conflicting offer={Offer} river legal={Legal}",
                                EngineName, liveCallKey, state.Legal.Flags);
                            return false;
                        }

                        SetStatus($"{EngineName} call deferred: retry queued");
                        log.Warning(
                            "[CallPromptRepair] engine={Engine} rejected batch events={EventCount} offer={IncrementalOffer} legal={Legal}; urgent retry queued",
                            EngineName,
                            batch.EventCount,
                            string.IsNullOrWhiteSpace(incrementalOfferKey) ? "-" : incrementalOfferKey,
                            state.Legal.Flags);
                        ScheduleUrgentRequeue(cfg, state, builtIn, state);
                        return false;
                    }

                    log.Warning(
                        "[CallPromptRepair] engine={Engine} corrected offer={SourceOffer} -> {CorrectedOffer}; preservedEvents={EventCount} legal={Legal}",
                        EngineName,
                        sourceOfferKey,
                        correctedOfferKey,
                        correctedBatch.EventCount,
                        state.Legal.Flags);
                    batch = correctedBatch;
                    lock (stateGate)
                        ClearDeferredCallLocked();
                }
            }

            MjaiEventBatch incrementalBatch = batch;
            if (processJustStarted
                && orderedReplayJournal.Count > 0)
            {
                batch = BuildOrderedReplayBatch(batch, state);
                log.Warning(
                    "[OrderedReplay] restored native session events={ReplayEvents} incremental={IncrementalEvents} decision={Decision}",
                    orderedReplayJournal.Count,
                    incrementalBatch.EventCount,
                    BatchExpectsDecision(batch.Json, state.OurSeat));
            }

            batchReady = Stopwatch.GetTimestamp();
            eventCount = batch.EventCount;
            sendBytes = Encoding.UTF8.GetByteCount(batch.Json);
            // A batch ending with our own committed chi/pon also demands a
            // decision: mjai has no draw between the call and its mandatory
            // discard, so Mortal answers the post-call dahai directly from the
            // call event. (The Akochan native path is unaffected: SendAkochanBatch
            // computes its own can_act flag and returns early on null responses.)
            expectsDecision = BatchExpectsDecision(batch.Json, state.OurSeat)
                || BatchEndsWithOwnCallDecision(batch.Json, state.OurSeat);
            if (TryGetLastOpponentCallOfferKey(batch.Json, state.OurSeat, out string incomingOfferKey))
            {
                lock (stateGate)
                {
                    // A new opponent discard supersedes an unconsumed response for
                    // an older call window. Do not clear on ordinary self decisions.
                    if (!string.Equals(deferredCallOfferKey, incomingOfferKey, StringComparison.Ordinal))
                        ClearDeferredCallLocked();
                }
            }
            log.Information(
                "[AITrace] id={RequestId} stage=batch_ready elapsedMs={ElapsedMs:F1} buildMs={BuildMs:F1} events={EventCount} bytes={Bytes} expectsDecision={ExpectsDecision} startsGame={StartsGame}",
                requestId, ElapsedMs(traceStart, batchReady), ElapsedMs(processReady, batchReady), eventCount, sendBytes, expectsDecision, batch.StartsGame);
            if (batch.EventCount == 0)
            {
                // Offer already synchronized (no re-append). If 298k answered
                // before the Chi/Pon UI appeared, publish that retained answer
                // instead of stranding the live prompt on events=0.
                if (IsLiveExternalCallPrompt(state)
                    && TryGetDeferredCallChoice(state, out choice))
                {
                    decisionReady = Stopwatch.GetTimestamp();
                    return true;
                }

                SetStatus(batch.Status);
                return false;
            }

            int timeout = processJustStarted || batch.StartsGame
                ? cfg.ExternalAiStartupTimeoutMs
                : cfg.ExternalAiTimeoutMs;
            processJustStarted = false;

            string? response;
            writeBegin = Stopwatch.GetTimestamp();
            log.Information(
                "[AITrace] id={RequestId} stage=stdin_write_begin elapsedMs={ElapsedMs:F1} bytes={Bytes} events={EventCount}",
                requestId, ElapsedMs(traceStart, writeBegin), sendBytes, eventCount);
            if (engineKind == ExternalEngineKind.AkochanComparison)
            {
                response = SendAkochanBatch(
                    batch.Json,
                    state.OurSeat,
                    timeout,
                    out writeEnd,
                    out softTimeoutExceeded);
                lock (trackerGate)
                    tracker.NoteBatchSent(batch.Json);
                if (response is null && !BatchExpectsDecision(batch.Json, state.OurSeat))
                {
                    SetStatus("Akochan connected: synchronization acknowledged");
                    return false;
                }
            }
            else
            {
                WriteTrace("mjai-send.jsonl", batch.Json);
                log.Debug("[ExternalAI:send] {Json}", Truncate(batch.Json, 1200));
                process.StandardInput.WriteLine(batch.Json);
                process.StandardInput.Flush();
                writeEnd = Stopwatch.GetTimestamp();
                lock (trackerGate)
                    tracker.NoteBatchSent(batch.Json);
                response = ReadLineWithTimeout(process, timeout);
            }
            responseReady = Stopwatch.GetTimestamp();
            log.Information(
                "[AITrace] id={RequestId} stage=response_ready elapsedMs={ElapsedMs:F1} writeMs={WriteMs:F1} waitMs={WaitMs:F1} received={Received} softTimeoutExceeded={SoftTimeoutExceeded}",
                requestId,
                ElapsedMs(traceStart, responseReady),
                ElapsedMs(writeBegin, writeEnd),
                ElapsedMs(writeEnd, responseReady),
                response is not null,
                softTimeoutExceeded);

            if (response is null)
                throw new TimeoutException($"No JSONL response within {timeout} ms");

            AppendOrderedJournal(incrementalBatch);

            receiveBytes = Encoding.UTF8.GetByteCount(response);
            WriteTrace("mjai-recv.jsonl", response);
            log.Debug("[ExternalAI:recv] {Json}", Truncate(response, 1200));
            JsonObject? responseObject = engineKind == ExternalEngineKind.AkochanComparison
                ? ParseAkochanResponse(response)
                : MjaiJson.ParseObject(response);
            parseReady = Stopwatch.GetTimestamp();
            log.Information(
                "[AITrace] id={RequestId} stage=response_parsed elapsedMs={ElapsedMs:F1} parseMs={ParseMs:F1} responseBytes={Bytes}",
                requestId, ElapsedMs(traceStart, parseReady), ElapsedMs(responseReady, parseReady), receiveBytes);
            if (responseObject is null)
                throw new InvalidDataException($"Invalid JSONL response: {Truncate(response, 180)}");

            // In mjai, reach is a two-step action: the bot first returns reach,
            // then chooses the discard after it consumes our reach event. FFXIV
            // needs one combined Riichi+discard choice, so complete the handshake
            // here and map the second dahai response to ActionKind.Riichi.
            if (engineKind != ExternalEngineKind.AkochanComparison
                && string.Equals(responseObject["type"]?.GetValue<string>(), "reach", StringComparison.Ordinal))
            {
                int actor = Math.Clamp(state.OurSeat, 0, 3);
                string reachBatch = MjaiJson.SerializeBatch([
                    MjaiJson.Object(new { type = "reach", actor })
                ]);
                WriteTrace("mjai-send.jsonl", reachBatch);
                log.Debug("[ExternalAI:send] {Json}", Truncate(reachBatch, 1200));
                process.StandardInput.WriteLine(reachBatch);
                process.StandardInput.Flush();

                string? reachResponse = ReadLineWithTimeout(process, cfg.ExternalAiTimeoutMs);
                if (reachResponse is null)
                    throw new TimeoutException($"No riichi discard response within {cfg.ExternalAiTimeoutMs} ms");

                WriteTrace("mjai-recv.jsonl", reachResponse);
                log.Debug("[ExternalAI:recv] {Json}", Truncate(reachResponse, 1200));
                JsonObject? discardResponse = MjaiJson.ParseObject(reachResponse);
                if (discardResponse is null
                    || !string.Equals(discardResponse["type"]?.GetValue<string>(), "dahai", StringComparison.Ordinal)
                    || !MjaiJson.TryParseTile(discardResponse["pai"]?.GetValue<string>(), out Tile riichiTile))
                    throw new InvalidDataException($"Mortal returned an invalid riichi discard: {Truncate(reachResponse, 180)}");

                choice = ActionChoice.DeclareRiichi(riichiTile, "Mortal: riichi");

                // The EMJ addon can expose the Riichi flag a few frames after the
                // hand itself becomes actionable. Mortal has already validated the
                // reach and selected the discard, so do not fall back merely because
                // the transient snapshot has not surfaced ActionFlags.Riichi yet.
                // The autoplay dispatcher waits for the actual Riichi UI flag before
                // clicking anything.
                if (!state.Hand.Contains(riichiTile))
                {
                    SetStatus($"Mortal riichi tile missing from hand: tile={riichiTile}");
                    return false;
                }

                lock (trackerGate)
                    tracker.NoteChoice(choice, state);
                SetStatus("Mortal connected: Riichi");
                return true;
            }

            string responseType = responseObject["type"]?.GetValue<string>() ?? "none";

            // A batch that ends with our own tsumo must be answered with a
            // concrete dahai/reach/hora/kan action; "none" is never legal
            // there. A broken session does not always announce itself on
            // stderr: a freshly restarted engine silently drops every event
            // sent before start_game and answers none forever (field capture
            // 2026-08-01 19:53:49-19:54:04, recovered only by a manual
            // resync). Schedule the same restart+resync as a stderr-reported
            // poisoning instead of leaving the overlay without instructions.
            if (string.Equals(responseType, "none", StringComparison.Ordinal)
                && (BatchEndsWithOwnDraw(batch.Json, state.OurSeat)
                    || BatchEndsWithOwnCallDecision(batch.Json, state.OurSeat)))
            {
                enginePoisoned = true;
                log.Warning(
                    "[EngineWatchdog] engine={Engine} answered none to our own draw decision; a restart and resync are scheduled",
                    EngineName);
                SetStatus($"{EngineName}: silent session loss detected; resynchronizing");
                return false;
            }

            bool isCallResponseType = responseType is "none" or "chi" or "pon" or "daiminkan" or "kan";
            if (expectsDecision
                && isCallResponseType
                && TryGetLastOpponentCallOfferKey(batch.Json, state.OurSeat, out string deferredKey))
            {
                // Always retain the 298k/Akochan call answer against the offer key.
                // Pass may already be visible (CanDeferCallResponse=false), but Chi↔Pon
                // flicker or a follow-up events=0 poll still needs this answer.
                lock (stateGate)
                {
                    if (!string.Equals(deferredCallOfferKey, deferredKey, StringComparison.Ordinal))
                        deferredCallServedKey = null;
                    deferredCallOfferKey = deferredKey;
                    deferredCallResponse = responseObject.DeepClone().AsObject();
                    deferredCallOfferDiscardCount = TryParseOfferActor(deferredKey, out int offerActor)
                        ? SeatRiverLength(state, offerActor)
                        : -1;
                }
                log.Information(
                    "[{Engine}DeferredCall] stored type={Type} key={Key} inferenceMs={InferenceMs} livePass={LivePass}",
                    EngineName, responseType, deferredKey, LastInferenceMs, state.Legal.Can(ActionFlags.Pass));

                if (CanDeferCallResponse(state))
                {
                    decisionReady = Stopwatch.GetTimestamp();
                    string pendingPrefix = engineKind == ExternalEngineKind.AkochanComparison
                        ? "Akochan pending"
                        : PendingStatusPrefix;
                    SetStatus($"{pendingPrefix}: awaiting matching call UI ({responseType})");
                    return false;
                }

                if (TryGetDeferredCallChoice(state, out choice))
                {
                    decisionReady = Stopwatch.GetTimestamp();
                    return true;
                }
            }

            if (string.Equals(responseType, "none", StringComparison.Ordinal)
                && !expectsDecision)
            {
                // A synchronization-only batch (for example our just-completed
                // dahai) is acknowledged by Mortal with type=none. This is not a
                // rejected action and must not poison the next real decision.
                // If a live call prompt is already up for a previously synced offer,
                // prefer any retained DeferredCall instead of stranding the UI.
                if (IsLiveExternalCallPrompt(state)
                    && TryGetDeferredCallChoice(state, out choice))
                {
                    decisionReady = Stopwatch.GetTimestamp();
                    return true;
                }

                SetStatus("Mortal connected: synchronization acknowledged");
                return false;
            }

            if (!MjaiActionMapper.TryMap(
                    responseObject, state, builtIn, out choice, EngineName,
                    allowUnreliableCallTarget: engineKind == ExternalEngineKind.AkochanComparison))
            {
                string type = responseObject["type"]?.GetValue<string>() ?? "unknown";
                string tile = responseObject["pai"]?.GetValue<string>() ?? "-";

                // Akochan has already consumed the authoritative mjai event. If
                // EMJ has not published the matching Riichi/Ron/Kan/call flag yet,
                // dropping this response makes the next snapshot produce an empty
                // batch and leaves autoplay with a synthetic Pass forever. Keep the
                // exact response tied to the unchanged board position and remap it
                // when the legal surface becomes authoritative.
                bool canDeferTransientLegalSurface = ShouldDeferTransientLegalSurface(
                    engineKind,
                    expectsDecision,
                    responseObject,
                    state);
                if (canDeferTransientLegalSurface)
                {
                    string position = PositionFingerprint(state);
                    lock (stateGate)
                    {
                        deferredDecisionPositionFingerprint = position;
                        deferredDecisionResponse = responseObject.DeepClone().AsObject();
                    }
                    SetStatus($"{EngineName} pending: waiting for legal surface for {type}");
                    log.Information(
                        "[{Engine}DeferredDecision] stored type={Type} tile={Tile} position={Position} legal={Legal}",
                        EngineName, type, tile, position, state.Legal.Flags);
                    return false;
                }

                // A discard recommendation for a tile that is not in the live
                // hand can never become legal by waiting: the engine's hand
                // model has diverged from the table (field capture 2026-08-01
                // 20:51:20, a phantom post-pon tsumo left an extra 9s in
                // Mortal's model and every later poll was rejected forever).
                // Keeping such a process alive strands the overlay, so schedule
                // the watchdog restart+resync instead.
                if (expectsDecision
                    && string.Equals(type, "dahai", StringComparison.Ordinal)
                    && MjaiJson.TryParseTile(tile, out Tile impossibleTile)
                    && !state.Hand.Contains(impossibleTile))
                {
                    enginePoisoned = true;
                    log.Warning(
                        "[EngineWatchdog] engine={Engine} recommended discarding {Tile} which is not in the live hand; a restart and resync are scheduled",
                        EngineName, tile);
                    SetStatus($"{EngineName}: hand model diverged; resynchronizing");
                    return false;
                }

                // The engine has already consumed the event batch, so killing
                // it here loses the whole hand and leaves Mortal waiting for a
                // new opening snapshot. Keep the synchronized process alive.
                SetStatus($"Mortal action rejected: type={type} tile={tile}");
                log.Warning("[ExternalAI] Mortal action rejected for current legal surface: type={Type} tile={Tile}", type, tile);
                return false;
            }

            // Do not release an accepted Chi/Pon/Kan merely because Akochan
            // returned a discard. The decision may still be waiting in the UI or
            // may be lost by a native-process restart. The record is released only
            // after a later snapshot proves that our river/hand advanced. Tsumo is
            // terminal and therefore needs no follow-up discard recovery.
            if (choice.Kind == ActionKind.Tsumo)
            {
                lock (stateGate)
                    ClearCommittedOwnCallState();
            }

            if (engineKind != ExternalEngineKind.AkochanComparison)
                lock (trackerGate)
                    tracker.NoteChoice(choice, state);
            if (engineKind == ExternalEngineKind.AkochanComparison)
            {
                log.Information(
                    "[AkochanDecision] action={Action} tile={Tile} source={Source} dispatch=false inferenceMs={InferenceMs}",
                    choice.Kind,
                    choice.DiscardTile?.ToString() ?? choice.Call?.ClaimedTile.ToString() ?? "-",
                    choice.Reasoning,
                    LastInferenceMs);
            }
            decisionReady = Stopwatch.GetTimestamp();
            log.Information(
                "[AITraceSummary] id={RequestId} engine={Engine} launchMs={LaunchMs:F1} processMs={ProcessMs:F1} batchMs={BatchMs:F1} writeMs={WriteMs:F1} aiWaitMs={AiWaitMs:F1} parseMs={ParseMs:F1} decisionMs={DecisionMs:F1} totalMs={TotalMs:F1} events={Events} sendBytes={SendBytes} receiveBytes={ReceiveBytes} action={Action}",
                requestId, EngineName,
                ElapsedMs(traceStart, launchReady),
                ElapsedMs(launchReady, processReady),
                ElapsedMs(processReady, batchReady),
                ElapsedMs(writeBegin, writeEnd),
                ElapsedMs(writeEnd, responseReady),
                ElapsedMs(responseReady, parseReady),
                ElapsedMs(parseReady, decisionReady),
                ElapsedMs(traceStart, decisionReady),
                eventCount, sendBytes, receiveBytes, choice.Kind);
            SetStatus($"{EngineName} connected: {choice.Kind}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or InvalidDataException or AggregateException)
        {
            SetStatus(ex.Message);
            log.Warning(ex, "[ExternalAI] mjai engine failed; using built-in policy");
            StopProcessCore(preserveSession: orderedReplayJournal.Count > 0);
            return false;
        }
    }

    private MjaiEventBatch BuildOrderedReplayBatch(MjaiEventBatch incremental, StateSnapshot state)
    {
        var merged = new JsonArray();
        foreach (JsonObject historical in orderedReplayJournal)
        {
            JsonObject clone = historical.DeepClone().AsObject();
            if (engineKind == ExternalEngineKind.AkochanComparison)
                clone["can_act"] = false;
            merged.Add(clone);
        }

        JsonArray current = JsonNode.Parse(incremental.Json) as JsonArray ?? new JsonArray();
        foreach (JsonNode? node in current)
        {
            if (node is JsonObject obj)
                merged.Add(obj.DeepClone());
        }

        // When the tracker has no new delta after a native crash, replay the
        // exact last decision boundary from the journal. This is safe because
        // every earlier event is explicitly non-actionable and the current FFXIV
        // snapshot still proves that a decision is required.
        if (incremental.EventCount == 0
            && (state.Legal.Can(ActionFlags.Discard) || state.Legal.Can(ActionFlags.Pass))
            && merged.Count > 0)
        {
            if (engineKind == ExternalEngineKind.AkochanComparison)
                merged[merged.Count - 1]!.AsObject()["can_act"] = true;
        }

        return new MjaiEventBatch(
            merged.ToJsonString(),
            merged.Count,
            StartsGame: true,
            Status: $"{EngineName} native session restored from ordered journal");
    }

    private void AppendOrderedJournal(MjaiEventBatch incremental)
    {
        if (incremental.EventCount == 0)
            return;

        JsonArray events = JsonNode.Parse(incremental.Json) as JsonArray
            ?? throw new InvalidDataException("Ordered journal batch is not a JSON array");

        int latestStartGame = -1;
        int latestStartKyoku = -1;
        for (int i = 0; i < events.Count; i++)
        {
            string? type = events[i]?["type"]?.GetValue<string>();
            if (type == "start_game") latestStartGame = i;
            if (type == "start_kyoku") latestStartKyoku = i;
        }

        if (latestStartGame >= 0)
            orderedReplayJournal.Clear();
        else if (latestStartKyoku >= 0)
        {
            // Retain only start_game identity before replacing the hand journal.
            JsonObject? startGame = orderedReplayJournal
                .LastOrDefault(e => e["type"]?.GetValue<string>() == "start_game");
            orderedReplayJournal.Clear();
            if (startGame is not null)
                orderedReplayJournal.Add(startGame.DeepClone().AsObject());
        }

        int begin = latestStartGame >= 0 ? latestStartGame : latestStartKyoku >= 0 ? latestStartKyoku : 0;
        for (int i = begin; i < events.Count; i++)
        {
            if (events[i] is JsonObject obj)
                orderedReplayJournal.Add(obj.DeepClone().AsObject());
        }
    }

    private string? SendAkochanBatch(
        string batchJson,
        int ourSeat,
        int timeout,
        out long writeCompletedTimestamp,
        out bool softTimeoutExceeded)
    {
        JsonArray events = JsonNode.Parse(batchJson) as JsonArray
            ?? throw new InvalidDataException("Akochan batch is not a JSON array");
        // Akochan does not commit the returned choice to its own hand. The
        // observed self dahai must therefore be sent like every other table
        // event; omitting it leaves 15 tiles at the following self draw.
        return SendAkochanEvents(
            process ?? throw new InvalidOperationException("Akochan process did not start"),
            events,
            ourSeat,
            timeout,
            out writeCompletedTimestamp,
            out softTimeoutExceeded);
    }

    private string? SendAkochanEvents(
        Process running,
        JsonArray events,
        int ourSeat,
        int timeout,
        out long writeCompletedTimestamp,
        out bool softTimeoutExceeded)
    {
        softTimeoutExceeded = false;
        if (events.Count == 0)
        {
            writeCompletedTimestamp = Stopwatch.GetTimestamp();
            return null;
        }

        bool expectsDecision = BatchExpectsDecision(events.ToJsonString(), ourSeat);
        for (int i = 0; i < events.Count; i++)
        {
            JsonObject evt = events[i]!.DeepClone().AsObject();
            // The bundled host uses can_act as the explicit decision boundary.
            // Set both true and false rather than relying on a missing property.
            // Own Chi/Pon remains synchronization-only because the originating
            // response already carried its mandatory follow-up discard.
            evt["can_act"] = i == events.Count - 1 && expectsDecision;
            string line = evt.ToJsonString();
            WriteTrace("akochan-send.jsonl", line);
            log.Debug("[Akochan:send] {Json}", Truncate(line, 1200));
            running.StandardInput.WriteLine(line);
        }
        running.StandardInput.Flush();
        writeCompletedTimestamp = Stopwatch.GetTimestamp();
        return expectsDecision
            ? ReadAkochanLineWithGrace(running, timeout, out softTimeoutExceeded)
            : null;
    }

    internal static JsonObject? ParseAkochanResponse(string response)
    {
        JsonArray? actions;
        try
        {
            actions = JsonNode.Parse(response) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
        if (actions is null || actions.Count == 0)
            return null;

        JsonObject? first = actions[0] as JsonObject;
        if (first is null)
            return null;

        JsonObject result = first.DeepClone().AsObject();
        string firstType = result["type"]?.GetValue<string>() ?? string.Empty;
        int firstActor = result["actor"]?.GetValue<int>() ?? -1;
        JsonObject? followingDiscard = actions
            .OfType<JsonObject>()
            .Skip(1)
            .FirstOrDefault(a =>
                string.Equals(a["type"]?.GetValue<string>(), "dahai", StringComparison.Ordinal)
                && (firstActor < 0 || (a["actor"]?.GetValue<int>() ?? firstActor) == firstActor));

        if (string.Equals(firstType, "reach", StringComparison.Ordinal)
            && result["pai"] is null
            && followingDiscard?["pai"] is { } reachPai)
        {
            result["pai"] = reachPai.DeepClone();
        }

        // Akochan returns a successful Chi/Pon and its mandatory discard in one
        // response array. Preserve that second action on the mapped call instead
        // of asking the native selector to act again with an own-call event,
        // which is not a valid selector entry state.
        if (firstType is "chi" or "pon"
            && followingDiscard?["pai"] is { } postCallPai)
        {
            result["_post_call_pai"] = postCallPai.DeepClone();
            if (followingDiscard["tsumogiri"] is { } tsumogiri)
                result["_post_call_tsumogiri"] = tsumogiri.DeepClone();
        }

        return result;
    }

    internal static bool IsLiveExternalCallPrompt(StateSnapshot state) =>
        !state.Legal.Can(ActionFlags.Discard)
        && state.Hand.Count > 0
        && state.Hand.Count % 3 == 1
        && state.Legal.Can(ActionFlags.Pass)
        && (state.Legal.Can(ActionFlags.Pon)
            || state.Legal.Can(ActionFlags.Chi)
            || state.Legal.Can(ActionFlags.MinKan));

    /// <summary>
    /// Resolves the call offer from the public river instead of EMJ candidate
    /// rows. Candidate ClaimedTile/FromSeat can be stale, duplicated or plain
    /// garbage (field capture 2026-08-01: candidate "Pon 6s from seat 1" while
    /// the river showed seat 3 discarding 8m). The river is the only surface
    /// that cannot name a tile that was never discarded, so when a live call
    /// prompt is visible and exactly one opponent river tip is callable with
    /// the current hand and legal flags, that actor+tile is the offer.
    /// </summary>
    internal static bool TryGetRiverAuthoritativeCallOffer(
        StateSnapshot state, out Tile tile, out int actor)
    {
        tile = default;
        actor = -1;
        if (!IsLiveExternalCallPrompt(state))
            return false;

        int ourSeat = Math.Clamp(state.OurSeat, 0, 3);
        bool chiOnly = state.Legal.Can(ActionFlags.Chi)
            && !state.Legal.Can(ActionFlags.Pon)
            && !state.Legal.Can(ActionFlags.MinKan);
        int chiActor = (ourSeat + 3) & 3;

        int matches = 0;
        for (int seat = 0; seat < Math.Min(4, state.Seats.Count); seat++)
        {
            if (seat == ourSeat || (chiOnly && seat != chiActor))
                continue;

            SeatView view = state.Seats[seat];
            if (view.Discards.Count == 0)
                continue;

            Tile tip = view.Discards[^1];
            if (tip.Id >= Tile.Count34 || !CanCallTile(state, tip, seat))
                continue;

            matches++;
            tile = tip;
            actor = seat;
        }

        if (matches != 1)
        {
            tile = default;
            actor = -1;
            return false;
        }
        return true;
    }

    /// <summary>
    /// True when a live call prompt provably corresponds to no claimable
    /// discard: Ron is not offered and no opponent river tip is callable with
    /// the current hand and legal flags. Such a prompt (observed in field
    /// captures as a repeating Kan+Pass surface backed only by a candidate row
    /// that contradicts every river) can never be answered by the external
    /// engine; Pass is the only valid decision.
    /// </summary>
    internal static bool IsProvablyUnclaimableCallPrompt(StateSnapshot state)
    {
        if (!IsLiveExternalCallPrompt(state)
            || state.Legal.Can(ActionFlags.Ron)
            || state.Legal.Can(ActionFlags.Tsumo))
            return false;

        int ourSeat = Math.Clamp(state.OurSeat, 0, 3);
        for (int seat = 0; seat < Math.Min(4, state.Seats.Count); seat++)
        {
            if (seat == ourSeat)
                continue;
            SeatView view = state.Seats[seat];
            if (view.Discards.Count == 0)
                continue;
            Tile tip = view.Discards[^1];
            if (tip.Id < Tile.Count34 && CanCallTile(state, tip, seat))
                return false;
        }
        return true;
    }

    /// <summary>
    /// ShouMinKan/AnKan surfaces appear on our draw before Discard is published.
    /// The closed hand has shape 3n+2 (for example 11 tiles after one open meld).
    /// </summary>
    internal static bool IsLiveOwnKanPrompt(StateSnapshot state) =>
        !state.Legal.Can(ActionFlags.Discard)
        && state.Hand.Count > 0
        && state.Hand.Count % 3 == 2
        && state.Legal.Can(ActionFlags.Pass)
        && (state.Legal.Can(ActionFlags.ShouMinKan) || state.Legal.Can(ActionFlags.AnKan));

    internal static bool IsUrgentExternalPrompt(StateSnapshot state) =>
        IsLiveExternalCallPrompt(state)
        || IsLiveOwnKanPrompt(state)
        || state.Legal.Can(ActionFlags.Ron)
        || state.Legal.Can(ActionFlags.Tsumo);

    internal static bool ShouldDeferTransientLegalSurface(
        ExternalEngineKind engineKind,
        bool expectsDecision,
        JsonObject response,
        StateSnapshot state)
    {
        if (!expectsDecision)
            return false;

        string type = response["type"]?.GetValue<string>() ?? "unknown";
        if (engineKind == ExternalEngineKind.AkochanComparison)
            return true;

        if (type == "hora")
        {
            int actor = response["actor"]?.GetValue<int>() ?? state.OurSeat;
            int target = response["target"]?.GetValue<int>() ?? actor;
            return actor == target
                ? !state.Legal.Can(ActionFlags.Tsumo)
                : !state.Legal.Can(ActionFlags.Ron);
        }

        // Our own closed/added kan is answered on the draw batch, but EMJ can
        // publish the AnKan/ShouMinKan flag a frame or two after the drawn hand
        // itself (field capture 2026-08-01 23:06:55: Mortal answered "ankan 7p"
        // to the fourth-7p tsumo while the surface still read Discard-only; the
        // answer was dropped, the flag appeared 0.2s later with nothing left to
        // map, and even manual resyncs looped on the same rejection forever).
        // Reaching this point with the flag already visible means the candidate
        // rows are still shaking, so deferral is the correct retry either way:
        // the retained response is remapped on every poll of this position.
        if (type is "ankan" or "kakan")
            return true;

        // A recommended discard of a tile that IS in the live hand can only
        // fail to map through a stale legal surface (missing Discard flag or a
        // transiently wrong DiscardableTiles row, field capture 2026-08-01
        // 23:05:03: "dahai 4p" rejected on a Discard+Riichi surface and the
        // whole turn passed without an instruction). Tiles absent from the
        // hand stay out: that case is the diverged-model watchdog's job.
        if (type == "dahai"
            && MjaiJson.TryParseTile(response["pai"]?.GetValue<string>(), out Tile deferredDiscard)
            && state.Hand.Contains(deferredDiscard))
        {
            return true;
        }

        bool isCallResponse = type is "none" or "chi" or "pon" or "daiminkan" or "kan";
        bool hasLiveCallPrompt = state.Legal.Can(ActionFlags.Pass)
            && (state.Legal.Flags & (ActionFlags.Pon | ActionFlags.Chi | ActionFlags.MinKan
                | ActionFlags.AnKan | ActionFlags.ShouMinKan | ActionFlags.Ron)) != 0;
        // After an open meld the closed hand is 10/7/4/1, not only 13. Losing the
        // call answer there previously left already-synced Chi/Pass prompts with
        // events=0 and no retained 298k decision.
        bool isCallDecisionShape = state.Hand.Count > 0 && state.Hand.Count % 3 == 1;
        if (isCallResponse && (isCallDecisionShape || hasLiveCallPrompt))
            return true;

        return false;
    }

    private void ScheduleUrgentRequeue(
        Configuration cfg,
        StateSnapshot state,
        ActionChoice builtIn,
        StateSnapshot requeueState)
    {
        string requeueFingerprint = Fingerprint(requeueState);
        lock (stateGate)
        {
            urgentRequeueState = requeueState;
            urgentRequeueFingerprint = requeueFingerprint;
            urgentRequeueCfg = cfg;
            urgentRequeueBuiltIn = builtIn;
            urgentRequeuePending = true;
        }
    }

    private bool TryGetUniqueLiveCallOfferKey(StateSnapshot state, out string key)
    {
        string[] liveTiles = state.Legal.PonCandidates
            .Concat(state.Legal.ChiCandidates)
            .Concat(state.Legal.KanCandidates)
            .Where(candidate => candidate.ClaimedTile.Id < Tile.Count34)
            .Select(candidate => MjaiJson.EncodeTile(candidate.ClaimedTile))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] observedMatches = recentOpponentDiscardKeys
            .Where(candidateKey =>
            {
                int separator = candidateKey.IndexOf('|');
                string tile = separator >= 0 ? candidateKey[(separator + 1)..] : string.Empty;
                return liveTiles.Contains(tile, StringComparer.Ordinal);
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (observedMatches.Length == 1)
        {
            key = observedMatches[0];
            return true;
        }

        if (!string.IsNullOrEmpty(latestOpponentDiscardKey)
            && observedMatches.Contains(latestOpponentDiscardKey, StringComparer.Ordinal))
        {
            // Multiple older opponent discards can remain in the response window
            // when TryChoose was not called between animations. The most recently
            // observed matching discard is the one that opened the currently
            // visible prompt.
            key = latestOpponentDiscardKey;
            return true;
        }

        // EMJ may expose the Pon/Chi/Kan buttons before candidate rows receive a
        // reliable ClaimedTile/FromSeat.  The opponent river tracker is already
        // ordered, so the latest concrete opponent discard is authoritative when
        // that tile is actually callable with the current hand and legal flags.
        // This prevents a visible call prompt from remaining permanently pending
        // merely because candidate metadata is empty, duplicated or one frame late.
        if (!string.IsNullOrEmpty(latestOpponentDiscardKey))
        {
            int separator = latestOpponentDiscardKey.IndexOf('|');
            if (separator > 0
                && int.TryParse(latestOpponentDiscardKey[..separator], out int latestActor)
                && latestActor != Math.Clamp(state.OurSeat, 0, 3)
                && MjaiJson.TryParseTile(latestOpponentDiscardKey[(separator + 1)..], out Tile latestTile)
                && CanCallTile(state, latestTile, latestActor))
            {
                key = latestOpponentDiscardKey;
                return true;
            }
        }
        // When the Chi button is already visible but EMJ has not published any
        // candidate rows yet, the offer is still fully determined by the rules:
        // Chi can only claim the immediately preceding player's latest discard.
        // Validate the tile directly against the closed hand instead of waiting for
        // delayed candidate metadata.  This is used only when there are no Chi rows,
        // so a concrete AtkValue-derived claimed tile always keeps priority.
        if (state.Legal.Can(ActionFlags.Chi)
            && state.Legal.ChiCandidates.Count == 0
            && TryResolveRuleAuthoritativeChiOfferKey(state, out key))
        {
            return true;
        }

        // Chi is only legal from the player immediately before us in turn order.
        // EMJ can duplicate or temporarily corrupt candidate FromSeat values while
        // opening the call modal, which previously made a single visible Chi offer
        // look ambiguous forever.  The claimed tile plus the rules-mandated actor
        // are authoritative here; consumed-tile variants do not change the offer.
        var chiOfferTiles = state.Legal.ChiCandidates
            .Where(candidate => candidate.ClaimedTile.Id < Tile.Count34)
            .Select(candidate => MjaiJson.EncodeTile(candidate.ClaimedTile))
            .Where(encoded => !string.IsNullOrWhiteSpace(encoded))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (state.Legal.Can(ActionFlags.Chi) && chiOfferTiles.Length == 1)
        {
            string chiTile = chiOfferTiles[0];
            bool competingTile = state.Legal.PonCandidates
                .Concat(state.Legal.KanCandidates)
                .Where(candidate => candidate.ClaimedTile.Id < Tile.Count34)
                .Select(candidate => MjaiJson.EncodeTile(candidate.ClaimedTile))
                .Any(encoded => !string.Equals(encoded, chiTile, StringComparison.Ordinal));

            if (!competingTile)
            {
                int actor = (Math.Clamp(state.OurSeat, 0, 3) + 3) & 3;
                key = $"{actor}|{chiTile}";
                if (!TryParseCallOfferKey(key, out int chiActor, out Tile chiOfferTile)
                    || !MjaiSessionTracker.CallOfferConflictsWithRiver(state, chiActor, chiOfferTile))
                {
                    return true;
                }
            }
        }

        var offers = state.Legal.PonCandidates
            .Concat(state.Legal.ChiCandidates)
            .Concat(state.Legal.KanCandidates)
            .Where(candidate => candidate.FromSeat >= 0 && candidate.ClaimedTile.Id < Tile.Count34)
            .Select(candidate =>
            {
                int actor = (Math.Clamp(state.OurSeat, 0, 3)
                    + Math.Clamp(candidate.FromSeat, 0, 3)) & 3;
                return $"{actor}|{MjaiJson.EncodeTile(candidate.ClaimedTile)}";
            })
            .Where(offerKey =>
                !TryParseCallOfferKey(offerKey, out int offerActor, out Tile offerTile)
                || !MjaiSessionTracker.CallOfferConflictsWithRiver(state, offerActor, offerTile))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (offers.Length == 1)
        {
            key = offers[0];
            return true;
        }

        // When candidate ClaimedTile rows are stale, the unique callable river
        // tip among opponents is the authoritative live offer.
        if (TryGetUniqueCallableRiverOfferKey(state, out key))
            return true;

        // The call candidate rows occasionally lose or duplicate FromSeat while an
        // opponent's riichi animation is opening the Pon/Chi/Pass prompt.  The
        // riichi declaration itself carries an exact actor and discard index, so use
        // that authoritative event instead of waiting forever for candidate metadata
        // to become unique.  This is not a heuristic: the offered tile must be the
        // declaring seat's final discard and must still satisfy the visible legal call.
        if (TryGetRiichiDiscardCallOfferKey(state, offers, out key))
            return true;

        key = string.Empty;
        return false;
    }

    private static bool TryParseCallOfferKey(string key, out int actor, out Tile tile)
    {
        actor = -1;
        tile = default;
        int separator = key.IndexOf('|');
        return separator > 0
            && int.TryParse(key[..separator], out actor)
            && actor is >= 0 and < 4
            && MjaiJson.TryParseTile(key[(separator + 1)..], out tile);
    }

    internal static bool TryGetRiichiDiscardCallOfferKey(
        StateSnapshot state, IReadOnlyCollection<string> candidateOffers, out string key)
    {
        key = string.Empty;
        var matches = new List<string>();
        int ourSeat = Math.Clamp(state.OurSeat, 0, 3);

        for (int actor = 0; actor < state.Seats.Count; actor++)
        {
            if (actor == ourSeat)
                continue;

            SeatView seat = state.Seats[actor];
            if (!seat.Riichi || !seat.Ippatsu)
                continue;
            if (seat.RiichiDiscardIndex < 0 || seat.RiichiDiscardIndex >= seat.Discards.Count)
                continue;
            if (seat.DiscardCount > 0 && seat.RiichiDiscardIndex != seat.DiscardCount - 1)
                continue;

            Tile tile = seat.Discards[seat.RiichiDiscardIndex];
            if (tile.Id >= Tile.Count34 || !CanCallTile(state, tile, actor))
                continue;

            string encoded = MjaiJson.EncodeTile(tile);
            if (candidateOffers.Count > 0
                && !candidateOffers.Any(offer => offer.EndsWith($"|{encoded}", StringComparison.Ordinal)))
            {
                continue;
            }

            matches.Add($"{actor}|{encoded}");
        }

        if (matches.Distinct(StringComparer.Ordinal).ToArray() is { Length: 1 } unique)
        {
            key = unique[0];
            return true;
        }

        return false;
    }

    internal static bool TryGetUniqueCallableRiverOfferKey(StateSnapshot state, out string key)
    {
        key = string.Empty;
        if (!IsLiveExternalCallPrompt(state))
            return false;

        int ourSeat = Math.Clamp(state.OurSeat, 0, 3);
        int seatCount = Math.Min(4, state.Seats.Count);
        bool chiOnly = state.Legal.Can(ActionFlags.Chi)
            && !state.Legal.Can(ActionFlags.Pon)
            && !state.Legal.Can(ActionFlags.MinKan);
        int chiActor = (ourSeat + 3) & 3;
        var matches = new List<string>();

        for (int actor = 0; actor < seatCount; actor++)
        {
            if (actor == ourSeat || (chiOnly && actor != chiActor))
                continue;

            SeatView seat = state.Seats[actor];
            if (seat.Discards.Count == 0)
                continue;

            Tile tile = seat.Discards[^1];
            if (tile.Id >= Tile.Count34 || !CanCallTile(state, tile, actor))
                continue;

            matches.Add($"{actor}|{MjaiJson.EncodeTile(tile)}");
        }

        string[] unique = matches.Distinct(StringComparer.Ordinal).ToArray();
        if (unique.Length != 1)
            return false;

        key = unique[0];
        return true;
    }

    internal static bool TryResolveRuleAuthoritativeChiOfferKey(
        StateSnapshot state, out string key)
    {
        key = string.Empty;
        if (!state.Legal.Can(ActionFlags.Chi) || state.Hand.Count == 0)
            return false;

        int ourSeat = Math.Clamp(state.OurSeat, 0, 3);
        int actor = (ourSeat + 3) & 3;
        if (actor >= state.Seats.Count)
            return false;

        SeatView seat = state.Seats[actor];
        if (seat.Discards.Count == 0)
            return false;

        Tile tile = seat.Discards[^1];
        if (tile.Id >= Tile.Count34 || !CanChiFromClosedHand(state.Hand, tile))
            return false;

        key = $"{actor}|{MjaiJson.EncodeTile(tile)}";
        return true;
    }

    internal static bool CanChiFromClosedHand(IReadOnlyList<Tile> hand, Tile tile)
    {
        if (tile.Id >= Tile.Count34 || tile.Suit == TileSuit.Honor)
            return false;
        return CallCandidateDeriver.Derive(hand, tile, fromSeat: 3).Chi.Count > 0;
    }

    internal static bool CanCallTile(StateSnapshot state, Tile tile, int actor = -1)
    {
        int copies = state.Hand.Count(t => t.Id == tile.Id);
        if (state.Legal.Can(ActionFlags.Pon) && copies >= 2)
            return true;
        if (state.Legal.Can(ActionFlags.MinKan) && copies >= 3)
            return true;
        if (state.Legal.Can(ActionFlags.Chi) && tile.Suit != TileSuit.Honor)
        {
            int ourSeat = Math.Clamp(state.OurSeat, 0, 3);
            int chiActor = (ourSeat + 3) & 3;
            if (actor >= 0 && actor != chiActor)
                return false;

            // The ordered river actor+tile is authoritative. Candidate rows can
            // retain the previous call's claimed tile after an accepted meld, so
            // validate the current discard directly against the closed hand.
            return CanChiFromClosedHand(state.Hand, tile);
        }
        return false;
    }

    internal static bool CanDeferCallResponse(StateSnapshot state)
    {
        // A deferred call response is valid only while FFXIV is between the
        // opponent discard and the authoritative call/pass surface. A 14/11/8/
        // 5/2-tile self-action shape with Discard legal is our own decision and
        // must never be labelled as an outstanding call prompt.
        return !state.Legal.Can(ActionFlags.Discard)
            && !state.Legal.Can(ActionFlags.Pass)
            && state.Hand.Count > 0
            && state.Hand.Count % 3 == 1;
    }

    private static bool TryGetLastOpponentCallOfferKey(string json, int ourSeat, out string key)
    {
        key = string.Empty;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray events || events.Count == 0)
                return false;
            if (events[^1] is not JsonObject last)
                return false;

            string type = last["type"]?.GetValue<string>() ?? string.Empty;
            int actor = last["actor"]?.GetValue<int>() ?? -1;
            string pai = last["pai"]?.GetValue<string>() ?? string.Empty;
            int seat = Math.Clamp(ourSeat, 0, 3);
            if (actor == seat || string.IsNullOrWhiteSpace(pai) || type is not ("dahai" or "kakan"))
                return false;

            key = $"{actor}|{pai}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool BatchExpectsDecision(string json, int ourSeat)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is not JsonArray events || events.Count == 0)
                return false;

            JsonObject? last = events[^1] as JsonObject;
            if (last is null)
                return false;

            string type = last["type"]?.GetValue<string>() ?? string.Empty;
            int actor = last["actor"]?.GetValue<int>() ?? -1;
            int seat = Math.Clamp(ourSeat, 0, 3);

            // Our draw requires a discard/riichi/tsumo decision. An opponent
            // discard can require pass/call/ron. Own Chi/Pon is synchronization
            // only: Akochan already returned the mandatory follow-up dahai in the
            // same response array as the call. Calling the selector with an own
            // Chi/Pon as the current action violates its accepted entry states.
            return (type == "tsumo" && actor == seat)
                || ((type == "dahai" || type == "kakan") && actor != seat);
        }
        catch (JsonException)
        {
            return true; // Preserve the old conservative behavior on malformed trace data.
        }
    }

    /// <summary>
    /// True when the batch's last event is our own tsumo, i.e. a discard-class
    /// decision the engine must answer with a concrete action. Unlike
    /// <see cref="BatchExpectsDecision"/> this excludes opponent discards,
    /// where "none" is the legitimate mjai encoding of Pass.
    /// </summary>
    internal static bool BatchEndsWithOwnDraw(string json, int ourSeat)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is not JsonArray events || events.Count == 0)
                return false;

            if (events[^1] is not JsonObject last)
                return false;

            return (last["type"]?.GetValue<string>() ?? string.Empty) == "tsumo"
                && (last["actor"]?.GetValue<int>() ?? -1) == Math.Clamp(ourSeat, 0, 3);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the batch's last event is our own committed chi/pon. In mjai
    /// there is no draw between the call and its mandatory discard, so the
    /// engine must answer this batch with the post-call dahai. Daiminkan is
    /// excluded because a rinshan tsumo arrives before that discard decision.
    /// </summary>
    internal static bool BatchEndsWithOwnCallDecision(string json, int ourSeat)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is not JsonArray events || events.Count == 0)
                return false;

            if (events[^1] is not JsonObject last)
                return false;

            return (last["type"]?.GetValue<string>() ?? string.Empty) is "chi" or "pon"
                && (last["actor"]?.GetValue<int>() ?? -1) == Math.Clamp(ourSeat, 0, 3);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void WriteTrace(string fileName, string line)
    {
        // JSONL trace persistence is diagnostic-only. Keep the required
        // Dalamud [Akochan:send]/[ExternalAI:recv] logs, but move disk writes
        // off the serialized engine task so a slow profile/AppData volume
        // cannot delay the next exact Akochan decision.
        pendingTraceWrites.Enqueue((fileName, $"{DateTime.UtcNow:O} {line}{Environment.NewLine}"));
        ScheduleTraceWriter();
    }

    private void ScheduleTraceWriter()
    {
        if (Interlocked.CompareExchange(ref traceWriterActive, 1, 0) != 0)
            return;
        _ = Task.Run(DrainTraceWrites);
    }

    private void DrainTraceWrites()
    {
        try
        {
            Directory.CreateDirectory(traceDirectory);
            while (pendingTraceWrites.TryDequeue(out var entry))
            {
                string path = Path.Combine(traceDirectory, entry.FileName);
                File.AppendAllText(path, entry.Line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Debug(ex, "[ExternalAI] could not write mjai trace");
        }
        finally
        {
            Volatile.Write(ref traceWriterActive, 0);
            if (!pendingTraceWrites.IsEmpty)
                ScheduleTraceWriter();
        }
    }

    private bool TryBuildLaunchSpec(Configuration cfg, out MjaiLaunchSpec launch, out string error)
    {
        if (engineKind == ExternalEngineKind.AkochanComparison)
            return AkochanRuntimeLocator.TryResolve(pluginAssemblyDirectory, cfg, out launch, out error);

        if (cfg.AiProvider == AiProvider.BundledMortal)
            return MortalRuntimeLocator.TryResolve(pluginAssemblyDirectory, cfg, out launch, out error);

        if (string.IsNullOrWhiteSpace(cfg.ExternalAiExecutable))
        {
            launch = null!;
            error = "External AI executable is not configured";
            return false;
        }

        string workingDirectory = ResolveWorkingDirectory(cfg);
        launch = new MjaiLaunchSpec(
            Executable: cfg.ExternalAiExecutable,
            Arguments: cfg.ExternalAiArguments ?? string.Empty,
            WorkingDirectory: workingDirectory,
            Identity: $"external:{cfg.ExternalAiExecutable}|{cfg.ExternalAiArguments}|{workingDirectory}",
            IsBundledMortal: false,
            Environment: new Dictionary<string, string>());
        error = string.Empty;
        return true;
    }

    internal static string PositionFingerprint(StateSnapshot state)
    {
        string hand = string.Join(',', state.Hand.Select(t => t.Id));
        string scores = string.Join(',', state.Scores);
        string dora = string.Join(',', state.DoraIndicators.Select(t => t.Id));
        string rivers = string.Join('|', state.Seats.Select(s =>
            $"{string.Join('.', s.Discards.Select(t => t.Id))}:{s.DiscardCount}:{s.Riichi}:{s.RiichiDiscardIndex}:" +
            string.Join(',', s.Melds.Select(MeldFingerprint))));
        string melds = string.Join('|', state.OurMelds.Select(MeldFingerprint));
        return string.Join('|',
            state.WallRemaining,
            state.OurSeat,
            state.DealerSeat,
            state.RoundWind,
            state.Honba,
            state.RiichiSticks,
            state.OurRiichi,
            scores,
            dora,
            hand,
            melds,
            rivers);
    }

    internal static string Fingerprint(StateSnapshot state)
    {
        string hand = string.Join(',', state.Hand.Select(t => t.Id));
        string scores = string.Join(',', state.Scores);
        string dora = string.Join(',', state.DoraIndicators.Select(t => t.Id));
        string rivers = string.Join('|', state.Seats.Select(s =>
            $"{string.Join('.', s.Discards.Select(t => t.Id))}:{s.DiscardCount}:{s.Riichi}:{s.RiichiDiscardIndex}:" +
            string.Join(',', s.Melds.Select(MeldFingerprint))));
        string melds = string.Join('|', state.OurMelds.Select(MeldFingerprint));
        string legal = string.Join('/',
            (int)state.Legal.Flags,
            string.Join('.', state.Legal.DiscardableTiles.Select(t => t.Id)),
            string.Join('.', state.Legal.PonCandidates.Select(CandidateFingerprint)),
            string.Join('.', state.Legal.ChiCandidates.Select(CandidateFingerprint)),
            string.Join('.', state.Legal.KanCandidates.Select(CandidateFingerprint)));
        // Addon state 6 and 30 are two transient UI surfaces for the same
        // discard decision. TurnIndex is currently not populated by the FFXIV
        // reader. Excluding both prevents the same board from being sent to
        // Mortal repeatedly while the UI transitions after a click.
        return string.Join('|',
            state.WallRemaining,
            state.OurSeat,
            state.DealerSeat,
            state.RoundWind,
            state.Honba,
            state.RiichiSticks,
            state.OurRiichi,
            scores,
            dora,
            hand,
            melds,
            rivers,
            legal);
    }

    private static string MeldFingerprint(Meld meld) =>
        $"{(int)meld.Kind}:{string.Join('-', meld.Tiles.Select(t => t.Id))}:{meld.ClaimedTile?.Id}:{meld.ClaimedFromSeat}";

    private static string CandidateFingerprint(MeldCandidate candidate) =>
        $"{(int)candidate.Kind}:{candidate.ClaimedTile.Id}:{string.Join('-', candidate.HandTiles.Select(t => t.Id))}:{candidate.FromSeat}";

    /// <summary>
    /// Classifies an engine stderr line as fatal session poisoning. libriichi
    /// prefixes both its own state-machine failures ("rule violation: attempt
    /// to witness the fifth 8s", field capture 2026-08-01 18:58) and the bot
    /// wrapper failures ("bot error: on event Tsumo ...") this way, and after
    /// either one the process only ever answers with empty/none events.
    /// </summary>
    internal static bool IndicatesEnginePoisoning(string? stderrLine) =>
        !string.IsNullOrWhiteSpace(stderrLine)
        && (stderrLine.Contains("rule violation", StringComparison.OrdinalIgnoreCase)
            || stderrLine.Contains("bot error", StringComparison.OrdinalIgnoreCase));

    internal static bool ShouldReplayCommittedCallAfterProcessStart(
        bool processJustStarted,
        ActionChoice? committedCall,
        bool committedCallCanReplay) =>
        processJustStarted && committedCall is not null && committedCallCanReplay;

    private void EnsureStartedCore(MjaiLaunchSpec launch, int playerId, bool preserveSession = false)
    {
        // The process is independent of seat. A later start_game event tells
        // Mortal the real player id and resets its PlayerState without paying
        // the Python/PyTorch process startup cost again.
        string effectiveIdentity = engineKind == ExternalEngineKind.AkochanComparison
            ? $"{launch.Identity}:seat={playerId}"
            : launch.Identity;
        if (process is { HasExited: false }
            && string.Equals(activeIdentity, effectiveIdentity, StringComparison.Ordinal))
        {
            return;
        }

        StopProcessCore(preserveSession, preserveCommittedOwnCall: true);
        var psi = new ProcessStartInfo
        {
            FileName = launch.Executable,
            Arguments = launch.Arguments.Replace(
                "{PLAYER_ID}",
                Math.Clamp(playerId, 0, 3).ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal),
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["DOMAN_MJAI_PLAYER_ID"] = playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var pair in launch.Environment)
            psi.Environment[pair.Key] = pair.Value;

        process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            if (IndicatesEnginePoisoning(e.Data))
            {
                enginePoisoned = true;
                log.Warning($"[EngineWatchdog] engine reported a broken session; a resync is scheduled: {e.Data}");
                return;
            }
            log.Debug($"[ExternalAI:stderr] {e.Data}");
        };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start external mjai engine");

        // The native Akochan build is already fixed at eight OpenMP workers
        // (one per physical CPU core on the supported runtime).  Give only
        // its decision process a higher scheduler priority so it receives
        // those cores promptly when FFXIV is busy; this does not alter the
        // model, tactics, or any emitted mjai state.
        if (engineKind == ExternalEngineKind.AkochanComparison)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.High;
                log.Information(
                    "[Akochan] process priority=High profile={Profile} OMP_NUM_THREADS=8 binding=cores",
                    launch.Identity.Contains("profile=Precision", StringComparison.Ordinal) ? "精度優先" : "高速");
            }
            catch (Win32Exception ex)
            {
                // Starting normally is still correct when Windows rejects a
                // priority change (for example under a restricted host).
                log.Warning(ex, "[Akochan] could not raise process priority");
            }
        }
        if (!string.IsNullOrEmpty(activeIdentity))
            Interlocked.Increment(ref restartCount);
        process.BeginErrorReadLine();
        if (launch.IsBundledMortal)
            LogMortalModelIdentity(launch);
        activeIdentity = effectiveIdentity;
        processJustStarted = true;
        if (!preserveSession)
            lock (trackerGate)
                tracker.Reset(preserveCommittedOwnCall: true);
        SetStatus(engineKind == ExternalEngineKind.AkochanComparison
            ? $"Akochan pending: starting process (PID {process.Id})"
            : launch.IsBundledMortal
            ? $"Mortal pending: loading model (PID {process.Id})"
            : $"External AI pending: starting process (PID {process.Id})");
    }


    private void LogMortalModelIdentity(MjaiLaunchSpec launch)
    {
        string modelPath = Path.Combine(launch.WorkingDirectory, "mortal.pth");
        string runtimeRoot = Directory.GetParent(launch.WorkingDirectory)?.FullName ?? launch.WorkingDirectory;
        string manifestPath = Path.Combine(runtimeRoot, "MORTAL_MODEL_MANIFEST.json");

        try
        {
            if (!File.Exists(modelPath))
            {
                log.Error("[MortalModel] status=MISSING path={Path}", modelPath);
                return;
            }

            var file = new FileInfo(modelPath);
            string actualHash;
            using (FileStream stream = File.OpenRead(modelPath))
                actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

            string modelName = "不明";
            int checkpoint = 0;
            long expectedBytes = -1;
            string expectedHash = string.Empty;
            if (File.Exists(manifestPath))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("model", out JsonElement modelElement))
                    modelName = modelElement.GetString() ?? "不明";
                if (root.TryGetProperty("checkpoint", out JsonElement checkpointElement))
                    checkpointElement.TryGetInt32(out checkpoint);
                if (root.TryGetProperty("bytes", out JsonElement bytesElement))
                    bytesElement.TryGetInt64(out expectedBytes);
                if (root.TryGetProperty("sha256", out JsonElement hashElement))
                    expectedHash = (hashElement.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            }

            bool manifestPresent = File.Exists(manifestPath);
            bool sizeMatches = expectedBytes >= 0 && expectedBytes == file.Length;
            bool hashMatches = expectedHash.Length == 64 && string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase);
            bool verified298k = manifestPresent
                && string.Equals(modelName, "VoidShine/mortal-298k", StringComparison.Ordinal)
                && checkpoint == 298000
                && sizeMatches
                && hashMatches;

            log.Information(
                "[MortalModel] status={Status} model={Model} checkpoint={Checkpoint} path={Path} bytes={Bytes} manifest={Manifest} sizeMatch={SizeMatch} hashMatch={HashMatch} sha256={Sha256}",
                verified298k ? "VERIFIED_298K" : manifestPresent ? "UNVERIFIED_OR_MISMATCH" : "NO_MANIFEST",
                modelName,
                checkpoint,
                file.FullName,
                file.Length,
                manifestPath,
                sizeMatches,
                hashMatches,
                actualHash);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MortalModel] status=VERIFY_ERROR path={Path} manifest={Manifest}", modelPath, manifestPath);
        }
    }

    private static string ResolveWorkingDirectory(Configuration cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ExternalAiWorkingDirectory)
            && Directory.Exists(cfg.ExternalAiWorkingDirectory))
            return cfg.ExternalAiWorkingDirectory;
        string? directory = Path.GetDirectoryName(cfg.ExternalAiExecutable);
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? directory
            : Environment.CurrentDirectory;
    }

    private string? ReadAkochanLineWithGrace(
        Process running,
        int softTimeoutMs,
        out bool softTimeoutExceeded)
    {
        softTimeoutMs = Math.Clamp(softTimeoutMs, 100, MaximumResponseTimeoutMs);
        long hardTimeoutCandidate = Math.Max(
            AkochanMinimumHardResponseTimeoutMs,
            (long)softTimeoutMs * 2L);
        int hardTimeoutMs = (int)Math.Clamp(
            hardTimeoutCandidate,
            softTimeoutMs,
            MaximumResponseTimeoutMs);

        long waitStarted = Stopwatch.GetTimestamp();
        Task<string?> readTask = running.StandardOutput.ReadLineAsync();
        if (readTask.Wait(softTimeoutMs))
        {
            softTimeoutExceeded = false;
            return readTask.Result;
        }

        softTimeoutExceeded = true;
        SetStatus($"Akochan pending: response exceeded {softTimeoutMs} ms; keeping session alive");
        log.Warning(
            "[AkochanTimeoutGuard] soft timeout exceeded softMs={SoftTimeoutMs} hardMs={HardTimeoutMs} pid={Pid}; waiting for the same in-flight response without restarting",
            softTimeoutMs,
            hardTimeoutMs,
            running.Id);

        int graceMs = hardTimeoutMs - softTimeoutMs;
        if (graceMs > 0 && readTask.Wait(graceMs))
        {
            string? lateResponse = readTask.Result;
            double elapsedMs = Stopwatch.GetElapsedTime(
                waitStarted,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            if (!string.IsNullOrWhiteSpace(lateResponse))
            {
                log.Information(
                    "[AkochanTimeoutGuard] late response recovered elapsedMs={ElapsedMs} pid={Pid} sessionPreserved=true",
                    elapsedMs,
                    running.Id);
                return lateResponse;
            }

            throw new InvalidDataException(
                $"AkochanプロセスがJSONL応答を返さず終了しました（{elapsedMs:F0} ms）");
        }

        throw new TimeoutException(
            $"No Akochan JSONL response within hard timeout {hardTimeoutMs} ms " +
            $"(soft timeout {softTimeoutMs} ms; session will be restarted)");
    }

    private static string? ReadLineWithTimeout(Process process, int timeoutMs)
    {
        timeoutMs = Math.Clamp(timeoutMs, 100, MaximumResponseTimeoutMs);
        var readTask = process.StandardOutput.ReadLineAsync();
        return readTask.Wait(timeoutMs) ? readTask.Result : null;
    }

    private static double ElapsedMs(long start, long end)
        => Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private void SetStatus(string value) => Volatile.Write(ref status, value);

    private void StopProcessCore(
        bool preserveSession = false,
        bool preserveCommittedOwnCall = false)
    {
        if (!preserveSession)
        {
            lock (trackerGate)
                tracker.Reset(preserveCommittedOwnCall);
            orderedReplayJournal.Clear();
            activeIdentity = string.Empty;
        }
        processJustStarted = false;
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        finally
        {
            process.Dispose();
            process = null;
        }
    }

    public void Dispose()
    {
        Process? running;
        lock (stateGate)
        {
            if (disposed)
                return;
            disposed = true;
            running = process;
        }

        // Interrupt a worker that may be waiting for stdout before taking the
        // process lock. This keeps plugin reload/unload responsive as well.
        try
        {
            if (running is { HasExited: false })
                running.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }

        lock (processGate)
            StopProcessCore();
    }
}
