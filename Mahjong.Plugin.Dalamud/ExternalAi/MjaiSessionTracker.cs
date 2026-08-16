using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mahjong.Plugin.Dalamud.ExternalAi;

internal readonly record struct MjaiEventBatch(
    string Json,
    int EventCount,
    bool StartsGame,
    string Status)
{
    public static MjaiEventBatch Empty(string status) => new("[]", 0, false, status);
}

/// <summary>
/// Converts successive public snapshots into the mjai event batches consumed by Mortal.
/// One stdin line is one JSON array and produces exactly one stdout action line.
/// </summary>
internal sealed class MjaiSessionTracker
{
    private StateSnapshot? previous;
    private bool gameStarted;
    private int currentDealer = -1;
    private Tile? pendingRiichiDiscard;
    private Tile? pendingOwnDiscard;
    // Exact call that was confirmed by the game UI. The post-call discard
    // surface can retain the old Pon/Chi/Pass window and the public meld reader
    // can lag by several frames, so snapshot-only inference is not authoritative
    // enough to advance the external engine. Consume this once on the first
    // post-call decision batch.
    private ActionChoice? pendingCommittedOwnCall;
    // Set after an own tsumo has been sent to Akochan. A following snapshot
    // must contain the corresponding self discard before opponent turns can
    // safely be appended to the same model round.
    private bool ownDrawOutstanding;
    // EMJ publishes our river entry before it removes the discarded tile from
    // the concealed-hand array. During that short transition the snapshot can
    // still be 14/11/8/5/2 tiles with Discard legal. Treating that stale array
    // as a new draw emits a duplicate tsumo and eventually pushes Akochan above
    // its private-hand limit. Keep the ordered session blocked until either the
    // hand shrinks or a genuinely different same-sized hand proves the next draw.
    private bool ownDiscardAwaitingHandCommit;
    private string? lastEmittedOwnMeldSignature;
    // Open melds successfully published to Mortal. Visual opponent-meld estimates
    // can appear a frame before ClaimedFromSeat / river context is known; keep
    // retrying until target can be filled, and never resend after success.
    private readonly HashSet<string> emittedOpenMeldKeys = new(StringComparer.Ordinal);
    // Atomic call offer captured at the first authoritative prompt snapshot.
    // It survives transient frames where EMJ clears candidate fields before the
    // prompt closes, preventing the AI instruction from disappearing mid-window.
    private CallOfferSnapshot? activeCallOffer;
    // A kyoku transition (wall replenished) was observed while the new hand was
    // not dealt yet (fewer than 13 tiles). start_kyoku cannot be built from
    // such a snapshot, so the transition is retained until the haipai appears.
    private bool pendingNewKyoku;
    // An opponent riichi discard is still callable (Ron/Pon/Kan/Chi) when it is
    // announced. Ending the batch with reach_accepted hides the dahai as the
    // decision boundary, so Mortal is never asked about the riichi tile and the
    // visible call window is lost (field capture 2026-08-01 16:24: a chi window
    // on a riichi 6s stayed unanswered for 9 seconds). The trailing
    // reach_accepted is therefore held back and emitted first in the next batch.
    private int deferredReachAcceptedActor = -1;
    // "actor|pai" of every dahai/kakan actually written to the engine in this
    // kyoku. `previous` advancing past a discard proves nothing about the
    // engine having seen it (a count-only river frame can absorb discards
    // without emitting them, field capture 2026-08-01 18:04), so the call
    // prompt repair must consult this set, not the snapshot bookkeeping.
    private readonly HashSet<string> sentDahaiKeys = new(StringComparer.Ordinal);
    // Encoded pai of the LAST dahai/kakan written to the engine per seat this
    // kyoku. A call always claims the target's most recent discard, so this is
    // the authoritative claimed tile when the visual meld decode is wrong
    // (field capture 2026-08-01 21:22:58: a committed 6m7m+8m chi was published
    // by EMJ as 5m6m7m claiming a never-discarded 5m).
    private readonly string?[] lastSentDahaiBySeat = new string?[4];
    // Number of our own call events (chi/pon/daiminkan/ankan) actually written
    // to the engine this kyoku. While state.OurMelds is larger, an own call is
    // still unsynced and no self-draw repair may be invented: the post-call
    // discard surface has no draw, and a phantom tsumo poisons the hand model.
    private int ownCallEventsEmitted;

    private readonly record struct CallOfferSnapshot(
        Tile Tile,
        int Actor,
        int WallRemaining,
        int ActorDiscardCount,
        ActionFlags LegalFlags);

    public void Reset(bool preserveCommittedOwnCall = false)
    {
        ActionChoice? committedCall = preserveCommittedOwnCall
            ? pendingCommittedOwnCall
            : null;

        previous = null;
        gameStarted = false;
        currentDealer = -1;
        pendingRiichiDiscard = null;
        pendingOwnDiscard = null;
        pendingCommittedOwnCall = committedCall;
        ownDrawOutstanding = false;
        ownDiscardAwaitingHandCommit = false;
        pendingNewKyoku = false;
        deferredReachAcceptedActor = -1;
        sentDahaiKeys.Clear();
        Array.Clear(lastSentDahaiBySeat);
        ownCallEventsEmitted = 0;
        emittedOpenMeldKeys.Clear();
        if (!preserveCommittedOwnCall)
        {
            lastEmittedOwnMeldSignature = null;
            activeCallOffer = null;
        }
    }

    public void NoteChoice(ActionChoice choice, StateSnapshot state)
    {
        if (choice.Kind is ActionKind.Pon or ActionKind.Chi or ActionKind.MinKan
            && choice.Call is { } acceptedCall)
        {
            // The FFXIV meld snapshot can advance just before AutoPlayLoop calls
            // NotifyCommittedAction. In that ordering BuildBatch has already
            // emitted the new meld from the snapshot. Queueing the same accepted
            // choice again would publish a duplicate Chi/Pon on the next batch.
            // Keep the atomic follow-up discard, but only queue the call event
            // when the ordered tracker has not already observed the exact meld.
            // Retain the exact committed call until the corresponding MJAI
            // event is known to have been emitted. Seeing the meld in `previous`
            // proves only that FFXIV exposed it, not that BuildBatch delivered it.
            pendingCommittedOwnCall = choice;

            // Akochan emits [chi|pon, dahai] atomically. Preserve that exact
            // mandatory discard as the next self dahai; there is no intervening
            // tsumo after Chi/Pon. Daiminkan still waits for the rinshan draw.
            pendingOwnDiscard = choice.Kind is ActionKind.Chi or ActionKind.Pon
                ? choice.PostCallDiscardTile
                : null;
            ownDrawOutstanding = false;
            ownDiscardAwaitingHandCommit = false;
            return;
        }

        if (choice.DiscardTile is not { } tile)
            return;

        if (choice.Kind is ActionKind.Discard or ActionKind.Riichi)
            pendingOwnDiscard = tile;
        if (choice.Kind == ActionKind.Riichi)
            pendingRiichiDiscard = tile;
    }

    public MjaiEventBatch BuildBatch(StateSnapshot state)
    {
        RefreshAtomicCallOffer(state);
        // The structural commit is authoritative. Once the concealed hand has
        // the post-discard shape, future actionable 14/11/8/5/2 snapshots may
        // again represent a real draw.
        if (ownDiscardAwaitingHandCommit
            && state.Hand.Count > 0
            && state.Hand.Count % 3 == 1)
        {
            ownDiscardAwaitingHandCommit = false;
        }
        // The current FFXIV reader often exposes only discard counts, not the
        // actual river tiles. Replaying a missing discard as pai="?" is not
        // legal mjai input: libriichi treats the unknown sentinel as a tile
        // index and can panic (for example len=34/index=37). In that situation
        // use a fresh, self-contained decision snapshot instead of attempting
        // to maintain an impossible incremental public history.
        // When no synchronized session exists, incomplete rivers require a
        // stateless bootstrap. Once Mortal is already following the current
        // hand, keep the session alive and append every concrete event we can
        // observe (our discard, calls, riichi and offered opponent discards).
        // Missing unrelated opponent river tiles are intentionally skipped;
        // restarting at start_kyoku would otherwise destroy our meld state.
        // A tracker reset after the external engine times out is different from
        // the opening of a hand.  `state.Hand` then represents the *current*
        // concealed hand, not the original 13 tiles.  Replaying the old river
        // and manufacturing each of our previous draws from state.Hand[^1]
        // adds the same tile repeatedly and eventually makes the engine see an
        // impossible 15+ tile hand.  Use the bounded stateless resync whenever
        // a newly-created tracker attaches to a hand that has already started.
        // It deliberately does not replay a partial historical hand.
        // A confirmed Chi/Pon can outlive a native Akochan process restart.
        // The old call buttons remain visible until the mandatory discard, while
        // the concealed hand has already shrunk to 11/8/5 tiles.  A normal
        // stateless bootstrap rejects that open-hand shape and strands autoplay.
        // When the exact accepted call is still available, rebuild only the
        // representable pre-call private hand and replay that call as the final
        // decision event.  No river order or unknown meld is invented.
        if (!gameStarted && previous is null && pendingCommittedOwnCall is not null)
        {
            MjaiEventBatch committedCallRecovery = BuildCommittedOwnCallRecoveryBatch(state);
            if (committedCallRecovery.EventCount > 0)
                return committedCallRecovery;
        }

        bool needsInitialResync = !gameStarted && previous is null
            && HasStartedHand(state);
        if ((HasIncompletePublicHistory(state) && (!gameStarted || previous is null))
            || needsInitialResync)
            return BuildStatelessDecisionBatch(state);

        var events = new List<JsonObject>(12);
        bool startsGame = false;

        // In the captured failure, we had emitted our tsumo, but its self
        // dahai never reached the snapshot before opponents advanced.  Feeding
        // those opponents into the same Akochan round leaves an extra private
        // tile and trips tehai_ana's num <= 15 assertion on the next draw.
        // A fresh valid round is faster and safer than waiting for that crash.
        if (previous is not null && ownDrawOutstanding && HasMissedOwnDiscard(previous, state))
            return BuildRoundResync(state, "Mortal round resynchronized after a missed self discard");

        // A snapshot gives us per-seat river counts, but not a global event
        // order.  If it advances any river by two or more tiles, replaying the
        // additions seat-by-seat invents an impossible turn sequence (for
        // example, two consecutive tsumo/dahai pairs for the same actor).
        // Libriichi relies on that sequence to maintain each hand and can
        // consequently reject a later decision with hai_int_to_str errors.
        // The missing ordering is not recoverable from this snapshot, so use
        // a fresh round only when the current closed hand can be represented
        // safely; never guess the intervening discards or calls.
        if (previous is not null && HasAmbiguousDiscardBacklog(previous, state))
            return BuildRoundResync(state, "Mortal round resynchronized after an unordered discard backlog");

        if (!gameStarted)
        {
            if (!CanBootstrap(state))
            {
                previous = state;
                return MjaiEventBatch.Empty("Mortal is waiting for the next fresh hand");
            }

            events.Add(MjaiJson.Object(new
            {
                type = "start_game",
                id = ClampSeat(state.OurSeat),
                names = new[] { "Doman-0", "Doman-1", "Doman-2", "Doman-3" },
            }));
            gameStarted = true;
            startsGame = true;
        }

        // A freshly emitted start_game must always be followed by start_kyoku
        // before any dora/tsumo/dahai event. `previous` can already hold a
        // pre-deal waiting snapshot of the same hand (wall unchanged), so the
        // wall-replenishment heuristic alone misses this boundary and Mortal
        // would receive events outside any round.
        bool newHand = previous is null
            || startsGame
            || pendingNewKyoku
            || IsNewHand(previous, state);
        if (newHand)
        {
            // The kyoku transition can be observed while the new hand is still
            // being dealt (0..12 tiles) or while stale melds from the previous
            // hand linger for a frame. start_kyoku requires exactly 13 concealed
            // tiles, so retain the transition and wait for the real haipai
            // instead of throwing away the whole next hand.
            if (state.Hand.Count is not (13 or 14) || state.OurMelds.Count != 0)
            {
                pendingNewKyoku = true;
                previous = state;
                return MjaiEventBatch.Empty("Mortal is waiting for the next hand's dealt tiles");
            }
            pendingNewKyoku = false;
            deferredReachAcceptedActor = -1;

            if (previous is not null)
                events.Add(MjaiJson.Object(new { type = "end_kyoku" }));

            currentDealer = InferDealer(state);
            emittedOpenMeldKeys.Clear();
            ownCallEventsEmitted = 0;
            events.Add(BuildStartKyoku(state, currentDealer));
            AppendBootstrapHistory(state, currentDealer, events);
            ownDiscardAwaitingHandCommit = false;
            ownDrawOutstanding = state.Legal.Can(ActionFlags.Discard) && state.Hand.Count == 14;
            previous = state;
            return BuildResult(events, startsGame, "Mortal hand initialized");
        }

        // Flush the riichi acceptance held back from the previous batch. The
        // riichi dahai has been answered (or the answer was retained as a
        // deferred call) by now, so the acceptance precedes all newer events.
        if (deferredReachAcceptedActor >= 0)
        {
            events.Add(MjaiJson.Object(new
            {
                type = "reach_accepted",
                actor = deferredReachAcceptedActor,
            }));
            deferredReachAcceptedActor = -1;
        }

        bool emittedCommittedOwnCall = TryAppendCommittedOwnCall(state, events);
        HashSet<int> calledSeats = FindChangedMeldSeats(previous!, state);
        if (emittedCommittedOwnCall)
            calledSeats.Add(ClampSeat(state.OurSeat));

        var withheldRiverSeats = new HashSet<int>();
        AppendDiscardsAndRiichi(previous!, state, calledSeats, events, withheldRiverSeats);
        AppendNewMelds(previous!, state, events, skipOurSeat: emittedCommittedOwnCall);
        AppendDoraTransitions(previous!, state, events);
        AppendOwnDraw(previous!, state, calledSeats, events);

        // EMJ can publish the completed concealed hand one frame before it
        // publishes ActionFlags.Discard. If a non-actionable snapshot was already
        // accepted as `previous`, the subsequent actionable snapshot has the same
        // hand and no river/meld delta, so the normal added-tile detector produces
        // an empty batch. The exact drawn tile is the appended hand slot used by
        // the existing start_kyoku/bootstrap path. Repair only when no own draw is
        // already outstanding and the concealed hand has a legal discard shape.
        bool repairedOwnDecision = false;
        int ourSeat = ClampSeat(state.OurSeat);
        bool isOwnDiscardDecision = state.Legal.Can(ActionFlags.Discard)
            && state.Hand.Count > 0
            && state.Hand.Count % 3 == 2;
        // Own discard must be answered before opponent turns are synchronized.
        // Otherwise Mortal consumes opponent dahai with can_act=false, later
        // Chi/Pass polls become events=0, and the 298k call answer is never kept.
        StateSnapshot? priorSnapshot = previous;
        bool withheldOpponentTurns = false;
        if (isOwnDiscardDecision)
            withheldOpponentTurns = StripOpponentTurnEvents(events, ourSeat);

        bool batchContainsOwnDecisionBoundary = ContainsOwnDrawEvent(events, ourSeat)
            || ContainsOwnDiscardEvent(events, ourSeat);

        // Our own committed chi/pon is itself the decision boundary: mjai has
        // no draw between the call and its mandatory discard, so inventing a
        // tsumo here adds a phantom tile to the engine's hand model. The pon
        // captured 2026-08-01 20:50:43 arrived via the snapshot meld diff
        // (AppendNewMelds), not TryAppendCommittedOwnCall, and the phantom
        // "tsumo 9s" made Mortal recommend discarding a 9s that was no longer
        // in the live hand ten turns later (permanent instruction loss).
        // An own meld visible in the snapshot but never written to the engine
        // (for example withheld because its visual decode failed validation)
        // is also a call boundary: the pending decision is the post-call
        // discard, and it persists across frames, not just the frame where the
        // meld diff appeared (field capture 2026-08-01 21:23:02: a withheld
        // chi plus an invented "tsumo 8s" one frame later poisoned Mortal).
        bool ownCallBoundary = emittedCommittedOwnCall
            || calledSeats.Contains(ourSeat)
            || ContainsOwnCallEvent(events, ourSeat)
            || GetMelds(state, ourSeat).Count > ownCallEventsEmitted;

        if (isOwnDiscardDecision && !batchContainsOwnDecisionBoundary && !ownCallBoundary)
        {
            if (!ownDrawOutstanding
                && !ownDiscardAwaitingHandCommit
                && pendingOwnDiscard is null)
            {
                // A non-actionable 14-tile snapshot can be accepted before EMJ
                // publishes the Discard flag. Opponent river updates that arrived
                // in the same snapshot are withheld above; restore only our tsumo
                // so Mortal answers with a discard rather than type=none.
                events.Add(MjaiJson.Object(new
                {
                    type = "tsumo",
                    actor = ourSeat,
                    pai = MjaiJson.EncodeTile(state.Hand[^1]),
                }));
                ownDrawOutstanding = true;
                repairedOwnDecision = true;
            }
            else if (events.Count > 0
                && state.OurMelds.Count == 0
                && state.Hand.Count is 13 or 14)
            {
                // The ordered tracker says that an earlier own draw/discard is
                // still outstanding, but FFXIV is authoritatively presenting a
                // fresh 14-tile discard decision. The missing boundary cannot be
                // reconstructed safely, so restart this round from the current
                // closed hand and request the discard immediately.
                // Empty batches (same stale pre-commit frame) must keep waiting.
                return BuildRoundResync(
                    state,
                    "Mortal round resynchronized after a lost self-draw decision boundary");
            }
        }

        // ShouMinKan/AnKan prompts appear on our draw before EMJ publishes Discard.
        // AppendOwnDraw requires ActionFlags.Discard, so an 11-tile kakan/ankan
        // surface otherwise produces events=0 and Mortal waits forever.
        bool isOwnKanPrompt = ExternalMjaiProcess.IsLiveOwnKanPrompt(state);
        bool handAdvancedDespitePendingDiscard = pendingOwnDiscard is not null
            && previous is not null
            && state.Hand.Count > previous.Hand.Count;
        if (isOwnKanPrompt
            && events.Count == 0
            && !batchContainsOwnDecisionBoundary
            && !emittedCommittedOwnCall
            && !ownDrawOutstanding
            && (pendingOwnDiscard is null || handAdvancedDespitePendingDiscard))
        {
            if (handAdvancedDespitePendingDiscard)
                pendingOwnDiscard = null;

            Tile drawTile = state.Hand[^1];
            if (previous is not null && TryFindAddedTile(previous.Hand, state.Hand, out Tile addedTile))
                drawTile = addedTile;

            events.Add(MjaiJson.Object(new
            {
                type = "tsumo",
                actor = ourSeat,
                pai = MjaiJson.EncodeTile(drawTile),
            }));
            ownDrawOutstanding = true;
            repairedOwnDecision = true;
        }

        DeferTrailingReachAccepted(events, ourSeat);

        previous = withheldOpponentTurns && priorSnapshot is not null
            ? WithholdOpponentRiverAdvances(priorSnapshot, state)
            : withheldRiverSeats.Count > 0 && priorSnapshot is not null
                ? WithholdOpponentRiverAdvances(priorSnapshot, state, withheldRiverSeats)
                : state;
        string status = repairedOwnDecision
            ? isOwnKanPrompt
                ? "Mortal: 加槓/暗槓表示の遅延後に自摸イベントを復元"
                : "Mortal: 合法手表示の遅延後に自摸イベントを復元"
            : events.Count == 0 && pendingOwnDiscard is not null
                ? "Akochan: 鳴き後の確定打牌がゲームへ反映されるのを待機"
                : events.Count == 0
                    ? "Mortal connected; waiting for a new event"
                    : "Mortal event batch ready";
        return BuildResult(events, startsGame, status);
    }

    /// <summary>
    /// Removes opponent turn events from an own-discard decision batch so Mortal
    /// answers our discard first. Matching river advances stay in
    /// <see cref="WithholdOpponentRiverAdvances"/> until the next BuildBatch.
    /// </summary>
    internal static bool StripOpponentTurnEvents(List<JsonObject> events, int ourSeat)
    {
        int before = events.Count;
        events.RemoveAll(evt =>
        {
            int actor = evt["actor"]?.GetValue<int>() ?? -1;
            if (actor < 0 || actor == ourSeat)
                return false;

            string type = evt["type"]?.GetValue<string>() ?? string.Empty;
            return type is "tsumo" or "dahai" or "kakan" or "reach" or "reach_accepted";
        });
        return events.Count < before;
    }

    /// <summary>
    /// Keeps opponent rivers at the previously synchronized point while adopting
    /// the rest of the live snapshot (our hand, scores, wall, etc.). When
    /// <paramref name="seatsToWithhold"/> is given, only those seats are kept
    /// back (used when a seat's new discards could not be resolved to concrete
    /// tiles yet and must be replayed on a later frame).
    /// </summary>
    internal static StateSnapshot WithholdOpponentRiverAdvances(
        StateSnapshot prior,
        StateSnapshot state,
        IReadOnlySet<int>? seatsToWithhold = null)
    {
        int ourSeat = ClampSeat(state.OurSeat);
        SeatView[] seats = state.Seats.ToArray();
        for (int seat = 0; seat < seats.Length && seat < prior.Seats.Count; seat++)
        {
            if (seat == ourSeat || (seatsToWithhold is not null && !seatsToWithhold.Contains(seat)))
                continue;

            SeatView old = prior.Seats[seat];
            seats[seat] = seats[seat] with
            {
                Discards = old.Discards,
                DiscardIsTedashi = old.DiscardIsTedashi,
                DiscardCount = old.DiscardCount,
                Riichi = old.Riichi,
                RiichiDiscardIndex = old.RiichiDiscardIndex,
            };
        }

        return state with { Seats = seats };
    }

    private static bool ContainsOwnDrawEvent(IReadOnlyList<JsonObject> events, int ourSeat)
    {
        foreach (JsonObject evt in events)
        {
            if (string.Equals(evt["type"]?.GetValue<string>(), "tsumo", StringComparison.Ordinal)
                && evt["actor"]?.GetValue<int>() == ourSeat)
                return true;
        }
        return false;
    }

    private static bool ContainsOwnDiscardEvent(IReadOnlyList<JsonObject> events, int ourSeat)
    {
        foreach (JsonObject evt in events)
        {
            if (string.Equals(evt["type"]?.GetValue<string>(), "dahai", StringComparison.Ordinal)
                && evt["actor"]?.GetValue<int>() == ourSeat)
                return true;
        }
        return false;
    }

    private static bool ContainsOwnCallEvent(IReadOnlyList<JsonObject> events, int ourSeat)
    {
        foreach (JsonObject evt in events)
        {
            if (evt["type"]?.GetValue<string>() is "chi" or "pon" or "daiminkan"
                && evt["actor"]?.GetValue<int>() == ourSeat)
                return true;
        }
        return false;
    }

    private static bool HasMissedOwnDiscard(StateSnapshot previous, StateSnapshot state)
    {
        int seat = ClampSeat(state.OurSeat);
        if (seat >= previous.Seats.Count || seat >= state.Seats.Count)
            return false;

        int oldDiscards = Math.Max(previous.Seats[seat].DiscardCount, previous.Seats[seat].Discards.Count);
        int newDiscards = Math.Max(state.Seats[seat].DiscardCount, state.Seats[seat].Discards.Count);
        return newDiscards <= oldDiscards && state.Hand.Count < previous.Hand.Count;
    }

    private static bool HasAmbiguousDiscardBacklog(StateSnapshot previous, StateSnapshot state)
    {
        int seatCount = Math.Min(previous.Seats.Count, state.Seats.Count);
        for (int seat = 0; seat < seatCount; seat++)
        {
            int oldDiscards = Math.Max(previous.Seats[seat].DiscardCount, previous.Seats[seat].Discards.Count);
            int newDiscards = Math.Max(state.Seats[seat].DiscardCount, state.Seats[seat].Discards.Count);
            if (newDiscards - oldDiscards > 1)
                return true;
        }

        return false;
    }

    private MjaiEventBatch BuildRoundResync(StateSnapshot state, string status)
    {
        // A 14-tile hand whose Discard surface is not published yet is a
        // transient decode frame: representing it either drops the drawn tile
        // (the next own dahai then references a tile the engine never saw and
        // poisons the session) or invents a tsumo outside our turn. Leave
        // `previous` untouched so the resync trigger re-fires on the next
        // poll, once the surface confirms whose turn it is.
        if (state.Hand.Count == 14 && !state.Legal.Can(ActionFlags.Discard))
            return MjaiEventBatch.Empty($"{status}; waiting for the drawn tile to become discardable");

        currentDealer = InferDealer(state);
        previous = state;
        ownDrawOutstanding = false;
        ownDiscardAwaitingHandCommit = false;
        deferredReachAcceptedActor = -1;

        // start_kyoku carries a closed 13/14-tile hand only.  An open hand or
        // another shape cannot be reconstructed without inventing meld order,
        // so keep the engine untouched until a later verified fresh hand.
        if (state.OurMelds.Count != 0 || state.Hand.Count is not (13 or 14))
            return MjaiEventBatch.Empty($"{status}; waiting for a safely representable hand");

        var events = new List<JsonObject>(3);
        if (gameStarted)
        {
            events.Add(MjaiJson.Object(new { type = "end_kyoku" }));
        }
        else
        {
            events.Add(MjaiJson.Object(new
            {
                type = "start_game",
                id = ClampSeat(state.OurSeat),
                names = new[] { "Doman-0", "Doman-1", "Doman-2", "Doman-3" },
            }));
            gameStarted = true;
        }
        events.Add(BuildStartKyoku(state, currentDealer));
        emittedOpenMeldKeys.Clear();
        ownCallEventsEmitted = 0;
        if (state.Hand.Count == 14)
        {
            events.Add(MjaiJson.Object(new
            {
                type = "tsumo",
                actor = ClampSeat(state.OurSeat),
                pai = MjaiJson.EncodeTile(state.Hand[^1]),
            }));
            ownDrawOutstanding = true;
        }
        return BuildResult(events, startsGame: false, status);
    }

    private static MjaiEventBatch BuildResult(List<JsonObject> events, bool startsGame, string status) =>
        events.Count == 0
            ? MjaiEventBatch.Empty(status)
            : new MjaiEventBatch(MjaiJson.SerializeBatch(events), events.Count, startsGame, status);

    private static bool HasIncompletePublicHistory(StateSnapshot state)
    {
        for (int seat = 0; seat < Math.Min(4, state.Seats.Count); seat++)
        {
            SeatView view = state.Seats[seat];
            if (view.DiscardCount > view.Discards.Count)
                return true;
        }
        return false;
    }

    private static bool HasStartedHand(StateSnapshot state) =>
        state.Seats.Any(view => Math.Max(view.DiscardCount, view.Discards.Count) > 0)
        || state.OurMelds.Count > 0;

    private MjaiEventBatch BuildCommittedOwnCallRecoveryBatch(StateSnapshot state)
    {
        ActionChoice? pending = pendingCommittedOwnCall;
        if (pending is null || pending.Call is not { } candidate)
            return MjaiEventBatch.Empty("Akochan: confirmed call recovery is not available");

        if (!state.Legal.Can(ActionFlags.Discard) || state.Hand.Count == 0)
            return MjaiEventBatch.Empty("Akochan: waiting for the mandatory post-call discard surface");

        string callType = pending.Kind switch
        {
            ActionKind.Chi => "chi",
            ActionKind.Pon => "pon",
            ActionKind.MinKan => "daiminkan",
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(callType) || candidate.FromSeat < 0)
        {
            pendingCommittedOwnCall = null;
            return MjaiEventBatch.Empty("Akochan: confirmed call recovery has an unsupported call kind");
        }

        // For the first open call of a hand, the private state before Chi/Pon is
        // exactly the current 11-tile hand plus the two consumed tiles.  For a
        // daiminkan decision, remove the newly drawn rinshan tile first and add
        // the three consumed tiles.  Only continue when that reconstruction is
        // exactly 13 tiles; otherwise preserving the live process is required and
        // no synthetic private tile is introduced.
        var initialHand = state.Hand.ToList();
        Tile? rinshanDraw = null;
        if (pending.Kind == ActionKind.MinKan)
        {
            rinshanDraw = initialHand[^1];
            initialHand.RemoveAt(initialHand.Count - 1);
        }
        initialHand.AddRange(candidate.HandTiles);
        if (initialHand.Count != 13)
        {
            return MjaiEventBatch.Empty(
                $"Akochan: open-hand resync is not exactly representable (closed={state.Hand.Count}, consumed={candidate.HandTiles.Length})");
        }

        int ourSeat = ClampSeat(state.OurSeat);
        int target = (ourSeat + Math.Clamp(candidate.FromSeat, 0, 3)) & 3;
        if (target == ourSeat)
            return MjaiEventBatch.Empty("Akochan: confirmed call recovery has an invalid target seat");

        int dealer = InferDealer(state);
        var events = new List<JsonObject>(7);
        events.Add(MjaiJson.Object(new
        {
            type = "start_game",
            id = ourSeat,
            names = new[] { "Doman-0", "Doman-1", "Doman-2", "Doman-3" },
        }));
        events.Add(BuildStartKyoku(state, dealer, initialHand));
        events.Add(MjaiJson.Object(new { type = "tsumo", actor = target, pai = "?" }));
        events.Add(MjaiJson.Object(new
        {
            type = "dahai",
            actor = target,
            pai = MjaiJson.EncodeTile(candidate.ClaimedTile),
            tsumogiri = false,
        }));

        var callEvent = new JsonObject
        {
            ["type"] = callType,
            ["actor"] = ourSeat,
            ["target"] = target,
            ["pai"] = MjaiJson.EncodeTile(candidate.ClaimedTile),
            ["consumed"] = new JsonArray(candidate.HandTiles
                .Select(tile => JsonValue.Create(MjaiJson.EncodeTile(tile)))
                .ToArray()),
        };
        events.Add(callEvent);

        if (rinshanDraw is { } drawn)
        {
            events.Add(MjaiJson.Object(new
            {
                type = "tsumo",
                actor = ourSeat,
                pai = MjaiJson.EncodeTile(drawn),
            }));
            ownDrawOutstanding = true;
        }
        else
        {
            ownDrawOutstanding = false;
        }

        gameStarted = true;
        currentDealer = dealer;
        previous = state;
        ownDiscardAwaitingHandCommit = false;
        // The recovery batch itself carried our call event.
        ownCallEventsEmitted = 1;
        pendingOwnDiscard = pending.Kind is ActionKind.Chi or ActionKind.Pon
            ? pending.PostCallDiscardTile
            : null;
        pendingCommittedOwnCall = null;
        return BuildResult(
            events,
            startsGame: true,
            "Akochan: confirmed call restored after process restart; requesting the mandatory discard");
    }

    private MjaiEventBatch BuildStatelessDecisionBatch(StateSnapshot state)
    {
        ownDiscardAwaitingHandCommit = false;
        deferredReachAcceptedActor = -1;
        if (!CanBootstrap(state))
            return MjaiEventBatch.Empty("Mortal stateless resync unavailable for this hand shape");

        var events = new List<JsonObject>(4);
        // gameStarted is committed only together with a successfully built
        // batch. Flipping it before the early return below leaked a phantom
        // "started" state: every later batch omitted start_game, and a freshly
        // restarted engine silently ignores all events before start_game and
        // answers none forever (field capture 2026-08-01 19:53:20-19:54:04).
        bool startsGame = !gameStarted;
        if (startsGame)
        {
            events.Add(MjaiJson.Object(new
            {
                type = "start_game",
                id = ClampSeat(state.OurSeat),
                names = new[] { "Doman-0", "Doman-1", "Doman-2", "Doman-3" },
            }));
        }

        int dealer = InferDealer(state);
        events.Add(BuildStartKyoku(state, dealer));

        int ourSeat = ClampSeat(state.OurSeat);
        if (state.Legal.Can(ActionFlags.Discard) && state.Hand.Count == 14)
        {
            events.Add(MjaiJson.Object(new
            {
                type = "tsumo",
                actor = ourSeat,
                pai = MjaiJson.EncodeTile(state.Hand[^1]),
            }));
            ownDrawOutstanding = true;
        }
        else if (TryGetCallOffer(state, out Tile calledTile, out int fromSeat))
        {
            int actor = ClampSeat(fromSeat);
            // Unknown is valid for an opponent draw, but never for dahai.
            events.Add(MjaiJson.Object(new { type = "tsumo", actor, pai = "?" }));
            events.Add(MjaiJson.Object(new
            {
                type = "dahai",
                actor,
                pai = MjaiJson.EncodeTile(calledTile),
                tsumogiri = false,
            }));
        }
        else
        {
            return MjaiEventBatch.Empty("Mortal stateless resync needs a concrete draw or call tile");
        }

        // Public river tiles are still incomplete, so start_kyoku is used as a
        // round-state resynchronization boundary. Keep start_game alive for the
        // whole table session: this avoids resetting Mortal's game identity and
        // model-side state on every decision while remaining valid when an
        // opponent discard tile is unavailable.
        previous = state;
        currentDealer = dealer;
        gameStarted = true;
        emittedOpenMeldKeys.Clear();
        ownCallEventsEmitted = 0;
        return BuildResult(events, startsGame, startsGame
            ? "Mortal game session started; round resynchronized"
            : "Mortal round resynchronized (public river tiles unavailable)");
    }

    /// <summary>
    /// Repairs a stale offered tile without discarding the ordered mjai history.
    /// Only the final opponent dahai/kakan event is changed. Actor, preceding
    /// discards, calls, riichi declarations, dora transitions and the original
    /// start_kyoku remain exactly as tracked.
    /// </summary>
    internal bool TryCorrectAuthoritativeCallPromptBatch(
        StateSnapshot state,
        MjaiEventBatch sourceBatch,
        out MjaiEventBatch correctedBatch,
        out string sourceOfferKey,
        out string correctedOfferKey)
    {
        if (!TryGetUniqueLiveCallOffer(state, out Tile calledTile, out int liveActor))
        {
            correctedBatch = sourceBatch;
            sourceOfferKey = string.Empty;
            correctedOfferKey = string.Empty;
            return false;
        }

        return TryCorrectAuthoritativeCallPromptBatch(
            state, sourceBatch, liveActor, calledTile,
            out correctedBatch, out sourceOfferKey, out correctedOfferKey);
    }

    internal bool TryCorrectAuthoritativeCallPromptBatch(
        StateSnapshot state,
        MjaiEventBatch sourceBatch,
        int liveActor,
        Tile calledTile,
        out MjaiEventBatch correctedBatch,
        out string sourceOfferKey,
        out string correctedOfferKey)
    {
        correctedBatch = sourceBatch;
        sourceOfferKey = string.Empty;
        correctedOfferKey = string.Empty;

        if (!IsAuthoritativeExternalCallPrompt(state)
            || liveActor < 0
            || liveActor >= 4
            || liveActor == ClampSeat(state.OurSeat)
            || calledTile.Id >= Tile.Count34
            || sourceBatch.Status.Contains("round resynchronized", StringComparison.OrdinalIgnoreCase)
            || sourceBatch.Status.Contains("stateless", StringComparison.OrdinalIgnoreCase))
            return false;

        string newPai = MjaiJson.EncodeTile(calledTile);
        if (string.IsNullOrWhiteSpace(newPai))
            return false;

        // Stale ClaimedTile must never rewrite or invent a discard that the
        // public river already contradicts (for example candidate "P" while the
        // river last is "C").
        if (CallOfferConflictsWithRiver(state, liveActor, calledTile))
            return false;

        // The call modal can become authoritative one frame before the public
        // river increments. When the ordered tracker has no new event, append
        // exactly the actor+tile exposed by the live candidate to the existing
        // synchronized session. This is not a stateless start_kyoku rebuild.
        if (sourceBatch.EventCount == 0)
        {
            return TryAppendAuthoritativeCallPromptBatch(
                state,
                liveActor,
                calledTile,
                out correctedBatch,
                out correctedOfferKey);
        }

        JsonArray? events;
        try
        {
            events = JsonNode.Parse(sourceBatch.Json) as JsonArray;
        }
        catch (JsonException)
        {
            return false;
        }
        if (events is null || events.Count == 0)
            return false;

        int ourSeat = ClampSeat(state.OurSeat);
        int offerIndex = -1;
        JsonObject? offer = null;
        int actor = -1;
        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is not JsonObject candidate)
                continue;
            string type = candidate["type"]?.GetValue<string>() ?? string.Empty;
            int candidateActor = candidate["actor"]?.GetValue<int>() ?? -1;
            if (type is not ("dahai" or "kakan")
                || candidateActor is < 0 or > 3
                || candidateActor == ourSeat)
                continue;

            offerIndex = i;
            offer = candidate;
            actor = candidateActor;
            break;
        }
        if (offerIndex < 0 || offer is null)
        {
            // History exists but no opponent discard boundary yet. Reuse the
            // authoritative append path instead of rejecting the whole batch.
            return TryAppendAuthoritativeCallPromptBatch(
                state,
                liveActor,
                calledTile,
                out correctedBatch,
                out correctedOfferKey);
        }

        // Actor and tile are a single identity. Do not retarget an already
        // ordered opponent event to another seat: a transient Chi/Pon label can
        // otherwise corrupt the whole turn sequence. When the ordered actor is
        // stale, append the authoritative live actor+tile instead of rejecting
        // the whole batch and losing the call prompt.
        if (actor != liveActor)
        {
            return TryAppendAuthoritativeCallPromptBatch(
                state,
                liveActor,
                calledTile,
                out correctedBatch,
                out correctedOfferKey);
        }

        string oldPai = offer["pai"]?.GetValue<string>() ?? string.Empty;
        JsonArray repaired = events.DeepClone().AsArray();
        JsonObject repairedOffer = repaired[offerIndex]!.AsObject();
        repairedOffer["pai"] = newPai;

        sourceOfferKey = $"{actor}|{(string.IsNullOrWhiteSpace(oldPai) ? "-" : oldPai)}";
        correctedOfferKey = $"{actor}|{newPai}";
        correctedBatch = new MjaiEventBatch(
            repaired.ToJsonString(),
            repaired.Count,
            sourceBatch.StartsGame,
            "Call offer corrected while preserving the ordered event history");

        previous = ReplaceLastConcreteDiscard(state, actor, calledTile);
        return true;
    }

    /// <summary>
    /// True only when the engine has actually received this offer as a dahai
    /// event. Checking the `previous` snapshot alone is not sufficient: a
    /// count-only river frame can advance `previous` past discards that were
    /// never emitted (field capture 2026-08-01 18:04, "Pon C" stuck for
    /// minutes), and skipping the authoritative re-append then loses the call.
    /// </summary>
    internal bool AlreadyHasCallOffer(int liveActor, Tile calledTile) =>
        previous is not null
        && HasConcreteLastDiscard(previous, liveActor, calledTile)
        && calledTile.Id < Tile.Count34
        && sentDahaiKeys.Contains($"{liveActor}|{MjaiJson.EncodeTile(calledTile)}");

    /// <summary>
    /// Records which dahai/kakan events were actually written to the engine.
    /// Must be called with the exact JSON batch that reached the engine's
    /// stdin. start_kyoku boundaries reset the record because offer keys are
    /// only meaningful within one hand.
    /// </summary>
    public void NoteBatchSent(string batchJson)
    {
        if (JsonNode.Parse(batchJson) is not JsonArray events)
            return;

        foreach (JsonNode? node in events)
        {
            if (node is not JsonObject evt)
                continue;

            string type = evt["type"]?.GetValue<string>() ?? string.Empty;
            if (type == "start_kyoku")
            {
                sentDahaiKeys.Clear();
                Array.Clear(lastSentDahaiBySeat);
                continue;
            }
            if (type is not ("dahai" or "kakan"))
                continue;

            int actor = evt["actor"]?.GetValue<int>() ?? -1;
            string pai = evt["pai"]?.GetValue<string>() ?? string.Empty;
            if (actor >= 0 && !string.IsNullOrEmpty(pai) && pai != "?")
            {
                sentDahaiKeys.Add($"{actor}|{pai}");
                if (actor < 4)
                    lastSentDahaiBySeat[actor] = pai;
            }
        }
    }

    /// <summary>
    /// True when the public river already shows a different last discard for
    /// <paramref name="liveActor"/> than the claimed call tile. Inventing the
    /// claimed tile would poison Mortal (for example "fifth P").
    /// </summary>
    internal static bool CallOfferConflictsWithRiver(
        StateSnapshot state, int liveActor, Tile calledTile)
    {
        if (liveActor < 0 || liveActor >= state.Seats.Count || calledTile.Id >= Tile.Count34)
            return false;

        SeatView seat = state.Seats[liveActor];
        if (seat.Discards.Count == 0)
            return false;

        Tile riverLast = seat.Discards[^1];
        return riverLast.Id < Tile.Count34 && riverLast != calledTile;
    }

    internal bool TryAppendAuthoritativeCallPromptBatch(
        StateSnapshot state,
        int liveActor,
        Tile calledTile,
        out MjaiEventBatch correctedBatch,
        out string correctedOfferKey)
    {
        correctedBatch = MjaiEventBatch.Empty("uninitialized call append");
        correctedOfferKey = string.Empty;

        if (!IsAuthoritativeExternalCallPrompt(state)
            || liveActor < 0
            || liveActor >= 4
            || liveActor == ClampSeat(state.OurSeat)
            || calledTile.Id >= Tile.Count34
            || !gameStarted)
            return false;

        // Mortal already consumed this actor+tile once. Re-appending the same
        // tsumo+dahai after BuildBatch goes empty counts the tile again and can
        // raise "attempt to witness the fifth …".
        if (AlreadyHasCallOffer(liveActor, calledTile))
            return false;

        // Candidate ClaimedTile can lag or retain a previous prompt's tile while
        // the public river already shows a different last discard. Never invent
        // a discard that contradicts the river.
        if (CallOfferConflictsWithRiver(state, liveActor, calledTile))
            return false;

        string newPai = MjaiJson.EncodeTile(calledTile);
        if (string.IsNullOrWhiteSpace(newPai))
            return false;

        var appended = new List<JsonObject>(2)
        {
            MjaiJson.Object(new { type = "tsumo", actor = liveActor, pai = "?" }),
            MjaiJson.Object(new
            {
                type = "dahai",
                actor = liveActor,
                pai = newPai,
                tsumogiri = false,
            }),
        };

        correctedOfferKey = $"{liveActor}|{newPai}";
        correctedBatch = BuildResult(
            appended,
            startsGame: false,
            "Call offer appended from the authoritative live prompt");
        previous = HasConcreteLastDiscard(state, liveActor, calledTile)
            ? state
            : AppendSyntheticDiscard(state, liveActor, calledTile);
        return true;
    }

    private static bool IsAuthoritativeExternalCallPrompt(StateSnapshot state) =>
        !state.Legal.Can(ActionFlags.Discard)
        && state.Hand.Count > 0
        && state.Hand.Count % 3 == 1
        && state.Legal.Can(ActionFlags.Pass)
        && (state.Legal.Can(ActionFlags.Pon)
            || state.Legal.Can(ActionFlags.Chi)
            || state.Legal.Can(ActionFlags.MinKan));

    private static bool TryGetUniqueLiveCallOffer(StateSnapshot state, out Tile tile, out int actor)
    {
        var offers = state.Legal.PonCandidates
            .Concat(state.Legal.ChiCandidates)
            .Concat(state.Legal.KanCandidates)
            .Where(candidate => candidate.FromSeat >= 0)
            .Select(candidate =>
            {
                int absoluteActor = ToAbsoluteSeat(state.OurSeat, candidate.FromSeat);
                Tile offered = candidate.ClaimedTile;
                if (offered.Id >= Tile.Count34
                    && absoluteActor >= 0
                    && absoluteActor < state.Seats.Count
                    && state.Seats[absoluteActor].Discards.Count > 0)
                {
                    // EMJ can expose Pon/Chi/Pass one refresh before it fills the
                    // candidate's claimed tile. The public river is already
                    // authoritative at that point, so recover the offered tile
                    // from the candidate's source seat instead of waiting for a
                    // later polling cycle or rebuilding the entire round.
                    offered = state.Seats[absoluteActor].Discards[^1];
                }
                return (Tile: offered, Actor: absoluteActor);
            })
            // Drop ClaimedTile rows that name a different tile than the seat's
            // current river tip. Those stale rows previously invented "1|P"
            // while the river showed "C" and poisoned Mortal.
            .Where(offer => offer.Tile.Id < Tile.Count34
                && !CallOfferConflictsWithRiver(state, offer.Actor, offer.Tile))
            .Distinct()
            .ToArray();

        if (offers.Length == 1)
        {
            tile = offers[0].Tile;
            actor = offers[0].Actor;
            return true;
        }

        // All candidate rows were dropped (river conflict) or were ambiguous.
        // The public river is authoritative: when exactly one opponent river
        // tip is callable with the current hand and legal flags, that is the
        // offer that opened the visible prompt. Without this fallback a false
        // ClaimedTile (for example "Pon 6s" while the river shows 8m) leaves
        // the whole call window without any offer and the instruction is lost.
        if (ExternalMjaiProcess.TryGetRiverAuthoritativeCallOffer(
                state, out Tile riverTile, out int riverActor))
        {
            tile = riverTile;
            actor = riverActor;
            return true;
        }

        tile = default;
        actor = -1;
        return false;
    }

    private static bool HasConcreteLastDiscard(StateSnapshot state, int actor, Tile tile)
    {
        if (actor < 0 || actor >= state.Seats.Count)
            return false;
        SeatView seat = state.Seats[actor];
        return seat.Discards.Count > 0 && seat.Discards[^1] == tile;
    }

    private static StateSnapshot AppendSyntheticDiscard(StateSnapshot state, int actor, Tile tile)
    {
        if (actor < 0 || actor >= state.Seats.Count)
            return state;

        SeatView[] seats = state.Seats.ToArray();
        SeatView seat = seats[actor];
        var discards = seat.Discards.ToList();
        var tedashi = seat.DiscardIsTedashi.ToList();
        discards.Add(tile);
        tedashi.Add(true);
        int nextCount = Math.Max(seat.DiscardCount, seat.Discards.Count) + 1;
        seats[actor] = seat with
        {
            Discards = discards,
            DiscardIsTedashi = tedashi,
            DiscardCount = nextCount,
        };
        return state with { Seats = seats };
    }

    private static StateSnapshot ReplaceLastConcreteDiscard(StateSnapshot state, int actor, Tile tile)
    {
        if (actor < 0 || actor >= state.Seats.Count)
            return state;

        SeatView seat = state.Seats[actor];
        if (seat.Discards.Count == 0)
            return state;

        SeatView[] seats = state.Seats.ToArray();
        Tile[] discards = seat.Discards.ToArray();
        discards[^1] = tile;
        seats[actor] = seat with { Discards = discards };
        return state with { Seats = seats };
    }

    private static int ToAbsoluteSeat(int ourSeat, int relativeSeat) =>
        (ClampSeat(ourSeat) + Math.Clamp(relativeSeat, 0, 3)) & 3;

    private bool TryGetCallOffer(StateSnapshot state, out Tile tile, out int fromSeat)
    {
        // The river outranks candidate metadata: a candidate row can name a
        // tile that no seat ever discarded, and building a synthetic dahai from
        // it would poison Mortal with an impossible event.
        if (ExternalMjaiProcess.TryGetRiverAuthoritativeCallOffer(
                state, out Tile riverTile, out int riverActor))
        {
            tile = riverTile;
            fromSeat = riverActor;
            return true;
        }

        foreach (MeldCandidate candidate in state.Legal.PonCandidates
            .Concat(state.Legal.ChiCandidates)
            .Concat(state.Legal.KanCandidates))
        {
            if (candidate.FromSeat < 0)
                continue;

            int absoluteSeat = ToAbsoluteSeat(state.OurSeat, candidate.FromSeat);
            Tile offered = candidate.ClaimedTile;
            if (offered.Id >= Tile.Count34
                && absoluteSeat >= 0
                && absoluteSeat < state.Seats.Count
                && state.Seats[absoluteSeat].Discards.Count > 0)
            {
                offered = state.Seats[absoluteSeat].Discards[^1];
            }

            if (offered.Id >= Tile.Count34
                || CallOfferConflictsWithRiver(state, absoluteSeat, offered))
                continue;

            tile = offered;
            fromSeat = absoluteSeat;
            return true;
        }

        if (activeCallOffer is { } retained && IsRetainedOfferValid(state, retained))
        {
            tile = retained.Tile;
            fromSeat = retained.Actor;
            return true;
        }

        tile = default;
        fromSeat = -1;
        return false;
    }

    private void RefreshAtomicCallOffer(StateSnapshot state)
    {
        bool prompt = IsAuthoritativeExternalCallPrompt(state);
        if (!prompt)
        {
            activeCallOffer = null;
            return;
        }

        // Prefer the river-proven offer: candidate rows can carry a stale or
        // fabricated ClaimedTile, and retaining that as the atomic offer makes
        // every later frame of the same window inherit the wrong tile.
        if (ExternalMjaiProcess.TryGetRiverAuthoritativeCallOffer(
                state, out Tile riverTile, out int riverActor))
        {
            int riverDiscardCount = Math.Max(
                state.Seats[riverActor].DiscardCount,
                state.Seats[riverActor].Discards.Count);
            activeCallOffer = new CallOfferSnapshot(
                riverTile, riverActor, state.WallRemaining, riverDiscardCount, state.Legal.Flags);
            return;
        }

        foreach (MeldCandidate candidate in state.Legal.PonCandidates
            .Concat(state.Legal.ChiCandidates)
            .Concat(state.Legal.KanCandidates))
        {
            if (candidate.FromSeat < 0)
                continue;
            int actor = ToAbsoluteSeat(state.OurSeat, candidate.FromSeat);
            Tile tile = candidate.ClaimedTile;
            if (tile.Id >= Tile.Count34
                && actor >= 0 && actor < state.Seats.Count
                && state.Seats[actor].Discards.Count > 0)
                tile = state.Seats[actor].Discards[^1];
            if (tile.Id >= Tile.Count34
                || CallOfferConflictsWithRiver(state, actor, tile))
                continue;

            int discardCount = actor >= 0 && actor < state.Seats.Count
                ? Math.Max(state.Seats[actor].DiscardCount, state.Seats[actor].Discards.Count)
                : -1;
            activeCallOffer = new CallOfferSnapshot(
                tile, actor, state.WallRemaining, discardCount, state.Legal.Flags);
            return;
        }
    }

    private static bool IsRetainedOfferValid(StateSnapshot state, CallOfferSnapshot offer)
    {
        if (!IsAuthoritativeExternalCallPrompt(state))
            return false;
        if ((state.Legal.Flags & offer.LegalFlags & (ActionFlags.Pon | ActionFlags.Chi | ActionFlags.MinKan)) == 0)
            return false;
        if (offer.Actor < 0 || offer.Actor >= state.Seats.Count)
            return false;
        int count = Math.Max(state.Seats[offer.Actor].DiscardCount, state.Seats[offer.Actor].Discards.Count);
        return count == offer.ActorDiscardCount && Math.Abs(state.WallRemaining - offer.WallRemaining) <= 1;
    }

    private static bool CanBootstrap(StateSnapshot state)
    {
        // A process restart can happen after the opening turn. We can still
        // recover a closed hand from the current 13/14-tile snapshot. Public
        // history is replayed when concrete river tiles are available; when the
        // reader only exposes discard counts, Mortal starts from the current
        // private hand instead of remaining disabled for the rest of the hand.
        return state.Hand.Count is 13 or 14 && state.OurMelds.Count == 0;
    }

    private static bool IsNewHand(StateSnapshot oldState, StateSnapshot state)
    {
        // A real kyoku transition replenishes the wall. River/meld readers can
        // transiently return zero/empty while EMJ refreshes an ordinary draw.
        // Treating that one-frame dropout as a new hand emits end_kyoku +
        // start_kyoku without the current tsumo, so Mortal returns none and the
        // visible instruction disappears. Wall replenishment is the only
        // authoritative transition signal available in StateSnapshot.
        return state.WallRemaining > oldState.WallRemaining + 8;
    }

    private static JsonObject BuildStartKyoku(StateSnapshot state, int inferredDealer)
    {
        // mjai start_kyoku requires exactly 13 concealed tiles for every seat.
        // EMJ can expose a transient 14-tile hand not only on the normal discard
        // surface, but also while a call/riichi popup is transitioning. Never put
        // the 14th tile into tehais; the current draw is represented by a separate
        // tsumo event when the surface is an own-turn discard decision.
        return BuildStartKyoku(state, inferredDealer, state.Hand.Take(13).ToArray());
    }

    private static JsonObject BuildStartKyoku(
        StateSnapshot state,
        int inferredDealer,
        IReadOnlyList<Tile> ourInitial)
    {
        if (ourInitial.Count != 13)
            throw new InvalidDataException($"Cannot build start_kyoku from {ourInitial.Count} hand tiles");

        var tehais = new object[4];
        int ourSeat = ClampSeat(state.OurSeat);
        for (int seat = 0; seat < 4; seat++)
        {
            tehais[seat] = seat == ourSeat
                ? ourInitial.Select(MjaiJson.EncodeTile).ToArray()
                : Enumerable.Repeat("?", 13).ToArray();
        }

        string dora = state.DoraIndicators.Count > 0 ? MjaiJson.EncodeTile(state.DoraIndicators[0]) : "?";
        int dealer = ClampSeat(inferredDealer);
        int[] scores = NormalizeScores(state.Scores);
        return MjaiJson.Object(new
        {
            type = "start_kyoku",
            bakaze = state.RoundWind switch { 1 => "S", 2 => "W", 3 => "N", _ => "E" },
            kyoku = dealer + 1,
            honba = Math.Max(0, state.Honba),
            kyotaku = Math.Max(0, state.RiichiSticks),
            oya = dealer,
            scores,
            dora_marker = dora,
            tehais,
        });
    }

    private static int[] NormalizeScores(IReadOnlyList<int> scores)
    {
        var result = new[] { 25000, 25000, 25000, 25000 };
        for (int i = 0; i < Math.Min(4, scores.Count); i++)
            result[i] = scores[i];
        return result;
    }

    private static void AppendBootstrapHistory(StateSnapshot state, int inferredDealer, List<JsonObject> events)
    {
        int ourSeat = ClampSeat(state.OurSeat);
        int dealer = ClampSeat(inferredDealer);
        int[] emitted = new int[4];
        int total = state.Seats.Sum(s => Math.Min(s.Discards.Count, Math.Max(s.DiscardCount, s.Discards.Count)));

        // At the first decision of a hand, our closed hand is still the initial 13 tiles.
        // Reconstruct the already-visible opening discards in turn order before presenting
        // the current call or draw opportunity to Mortal.
        for (int turn = 0; turn < total; turn++)
        {
            int seat = (dealer + turn) % 4;
            if (seat >= state.Seats.Count || emitted[seat] >= state.Seats[seat].Discards.Count)
                continue;

            Tile discarded = state.Seats[seat].Discards[emitted[seat]];
            bool tedashi = emitted[seat] < state.Seats[seat].DiscardIsTedashi.Count
                && state.Seats[seat].DiscardIsTedashi[emitted[seat]];

            // Our own historical turns cannot be replayed faithfully: start_kyoku
            // already carries the CURRENT 13 concealed tiles, not the original
            // haipai, so a past discard is a tile the engine's hand does not
            // contain. Replaying "draw the discarded tile, then discard it"
            // keeps the engine's tile accounting exactly consistent with both
            // the tehais and the visible river (the previous shape - always
            // drawing the current last hand tile - discarded tiles from void
            // and poisoned the session, field capture 2026-08-01 19:53:20).
            if (seat == ourSeat)
            {
                events.Add(MjaiJson.Object(new
                {
                    type = "tsumo",
                    actor = seat,
                    pai = MjaiJson.EncodeTile(discarded),
                }));
                events.Add(MjaiJson.Object(new
                {
                    type = "dahai",
                    actor = seat,
                    pai = MjaiJson.EncodeTile(discarded),
                    tsumogiri = true,
                }));
                emitted[seat]++;
                continue;
            }

            events.Add(MjaiJson.Object(new
            {
                type = "tsumo",
                actor = seat,
                pai = "?",
            }));
            events.Add(MjaiJson.Object(new
            {
                type = "dahai",
                actor = seat,
                pai = MjaiJson.EncodeTile(discarded),
                tsumogiri = !tedashi,
            }));
            emitted[seat]++;
        }

        if (state.Legal.Can(ActionFlags.Discard) && state.Hand.Count == 14)
        {
            events.Add(MjaiJson.Object(new
            {
                type = "tsumo",
                actor = ourSeat,
                pai = MjaiJson.EncodeTile(state.Hand[^1]),
            }));
        }
    }

    private static void AppendDoraTransitions(StateSnapshot oldState, StateSnapshot state, List<JsonObject> events)
    {
        for (int i = oldState.DoraIndicators.Count; i < state.DoraIndicators.Count; i++)
        {
            events.Add(MjaiJson.Object(new
            {
                type = "dora",
                dora_marker = MjaiJson.EncodeTile(state.DoraIndicators[i]),
            }));
        }
    }

    private static HashSet<int> FindChangedMeldSeats(StateSnapshot oldState, StateSnapshot state)
    {
        var changed = new HashSet<int>();
        for (int seat = 0; seat < 4; seat++)
        {
            if (!MeldListsEquivalent(GetMelds(oldState, seat), GetMelds(state, seat)))
                changed.Add(seat);
        }
        return changed;
    }

    private bool TryAppendCommittedOwnCall(StateSnapshot state, List<JsonObject> events)
    {
        ActionChoice? pending = pendingCommittedOwnCall;
        if (pending is null || pending.Call is not { } candidate)
            return false;
        ActionChoice choice = pending;

        string type = choice.Kind switch
        {
            ActionKind.Chi => "chi",
            ActionKind.Pon => "pon",
            ActionKind.MinKan => "daiminkan",
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(type))
        {
            pendingCommittedOwnCall = null;
            return false;
        }

        int ourSeat = ClampSeat(state.OurSeat);
        string signature = OwnMeldSignature(choice, ourSeat);
        if (string.Equals(lastEmittedOwnMeldSignature, signature, StringComparison.Ordinal))
        {
            pendingCommittedOwnCall = null;
            ownDrawOutstanding = false;
            return false;
        }

        int target = candidate.FromSeat < 0
            ? -1
            : (ourSeat + Math.Clamp(candidate.FromSeat, 0, 3)) & 3;
        var evt = new JsonObject
        {
            ["type"] = type,
            ["actor"] = ourSeat,
            ["pai"] = MjaiJson.EncodeTile(candidate.ClaimedTile),
            ["consumed"] = new JsonArray(candidate.HandTiles
                .Select(tile => JsonValue.Create(MjaiJson.EncodeTile(tile)))
                .ToArray()),
        };
        // Mortal rejects chi/pon/daiminkan without target. Prefer waiting for a
        // later frame over poisoning the ordered session with a partial event.
        if (target < 0
            && (choice.Kind is ActionKind.Chi or ActionKind.Pon or ActionKind.MinKan))
            return false;

        if (target >= 0 && choice.Kind is not ActionKind.ShouMinKan)
            evt["target"] = target;

        events.Add(evt);
        lastEmittedOwnMeldSignature = signature;
        emittedOpenMeldKeys.Add($"{ourSeat}|{signature}");
        ownCallEventsEmitted++;
        pendingCommittedOwnCall = null;
        ownDrawOutstanding = false;
        return true;
    }

    private static string OwnMeldSignature(ActionChoice choice, int ourSeat)
    {
        if (choice.Call is not { } candidate)
            return string.Empty;
        string kind = choice.Kind switch
        {
            ActionKind.Chi => "chi",
            ActionKind.Pon => "pon",
            ActionKind.MinKan => "daiminkan",
            _ => choice.Kind.ToString(),
        };
        var tiles = candidate.HandTiles
            .Append(candidate.ClaimedTile)
            .Select(tile => tile.Id)
            .OrderBy(id => id)
            .ToArray();
        int absoluteFromSeat = candidate.FromSeat < 0
            ? -1
            : (ClampSeat(ourSeat) + Math.Clamp(candidate.FromSeat, 0, 3)) & 3;
        return $"{kind}|{absoluteFromSeat}|{string.Join(',', tiles)}";
    }

    private void AppendNewMelds(
        StateSnapshot oldState,
        StateSnapshot state,
        List<JsonObject> events,
        bool skipOurSeat = false)
    {
        int ourSeat = ClampSeat(state.OurSeat);
        for (int seat = 0; seat < 4; seat++)
        {
            if (skipOurSeat && seat == ourSeat)
                continue;
            var oldMelds = GetMelds(oldState, seat);
            var melds = GetMelds(state, seat);
            int common = Math.Min(oldMelds.Count, melds.Count);
            for (int i = 0; i < common; i++)
            {
                if (!MeldEquivalent(oldMelds[i], melds[i]))
                    TryEmitMeld(melds[i], seat, oldState, state, events);
            }
            for (int i = common; i < melds.Count; i++)
                TryEmitMeld(melds[i], seat, oldState, state, events);

            // Retry previously skipped open melds (unknown target) once river
            // context catches up. Only attempt keys not yet successfully sent.
            for (int i = 0; i < melds.Count; i++)
            {
                string key = $"{seat}|{OwnMeldSignature(melds[i])}";
                if (emittedOpenMeldKeys.Contains(key))
                    continue;
                // Already considered above as new/changed; still retry stable
                // entries that failed TryBuild on an earlier frame.
                if (i < oldMelds.Count && MeldEquivalent(oldMelds[i], melds[i]))
                    TryEmitMeld(melds[i], seat, oldState, state, events);
            }
        }
    }

    private void TryEmitMeld(
        Meld meld,
        int seat,
        StateSnapshot oldState,
        StateSnapshot state,
        List<JsonObject> events)
    {
        string key = $"{seat}|{OwnMeldSignature(meld)}";
        if (emittedOpenMeldKeys.Contains(key))
            return;

        // Opponent visual estimates can invent a fifth copy of a tile. Prefer
        // omitting that meld over poisoning Mortal's ordered session.
        if (seat != ClampSeat(state.OurSeat)
            && Mahjong.Plugin.Dalamud.GameState.OpponentMeldTileBudget.MeldExceedsBudget(meld, state, seat))
            return;

        bool isOurSeat = seat == ClampSeat(state.OurSeat);
        if (!TryBuildMeldEvent(meld, seat, oldState, state, out JsonObject evt))
        {
            // Our own meld can still be reconstructed authoritatively even
            // when the visual decode is unusable (for example an unknown
            // target); opponents have no hand delta to reconstruct from.
            if (!isOurSeat || !TryRepairOwnMeldEvent(seat, oldState, state, events, out evt))
                return;
        }
        // A chi/pon/daiminkan is only real when the engine has already seen
        // the claimed discard from the target seat. EMJ meld data shakes
        // during call animations (field capture 2026-08-01 18:57: a committed
        // 7p chi flickered to 6s7s8s claiming a never-discarded 6s), and
        // emitting the invented event corrupts the engine's tile counts for
        // the rest of the hand ("fifth tile" rule violation). Skip the frame
        // and retry once the visual data settles on a claimable tile.
        else if (evt["type"]?.GetValue<string>() is "chi" or "pon" or "daiminkan"
            && !ClaimedDiscardIsKnownToEngine(evt, events))
        {
            LastRejectedMeldEvent = evt.ToJsonString();

            // In suggest-only play there is no committed-call notification, so
            // the visual decode is the only meld source — and it can be plain
            // wrong, not just late (field capture 2026-08-01 21:22:58: a real
            // 6m7m+8m chi was published as 5m6m7m claiming a never-discarded
            // 5m, permanently withholding the call and stranding the session).
            // Rebuild our own meld from authoritative facts instead: the tiles
            // that actually left the concealed hand plus the last discard the
            // engine was told about for each opponent.
            if (!isOurSeat || !TryRepairOwnMeldEvent(seat, oldState, state, events, out evt))
                return;
        }

        events.Add(evt);
        emittedOpenMeldKeys.Add(key);
        if (isOurSeat)
        {
            lastEmittedOwnMeldSignature = OwnMeldSignature(meld);
            if (evt["type"]?.GetValue<string>() is "chi" or "pon" or "daiminkan" or "ankan")
                ownCallEventsEmitted++;
        }
    }

    /// <summary>
    /// Reconstructs our own chi/pon/daiminkan from authoritative evidence when
    /// the visual meld decode fails validation. The consumed tiles are the
    /// exact multiset that left the concealed hand between <paramref name="oldState"/>
    /// and <paramref name="state"/>; the claimed tile must be the most recent
    /// discard the engine knows for the target seat. Succeeds only when
    /// exactly one opponent's last known discard completes a legal meld with
    /// those consumed tiles.
    /// </summary>
    private bool TryRepairOwnMeldEvent(
        int seat,
        StateSnapshot oldState,
        StateSnapshot state,
        List<JsonObject> events,
        out JsonObject evt)
    {
        evt = null!;

        List<Tile> consumed = MultisetDifference(oldState.Hand, state.Hand);
        if (consumed.Count is not (2 or 3))
            return false;

        // Tiles leaving the hand together with a river advance would be a
        // discard, not a call.
        if (seat < oldState.Seats.Count && seat < state.Seats.Count
            && state.Seats[seat].DiscardCount > oldState.Seats[seat].DiscardCount)
            return false;

        int match = -1;
        Tile claimedTile = default;
        string? matchType = null;
        string? matchPai = null;
        for (int target = 0; target < 4; target++)
        {
            if (target == seat)
                continue;
            string? pai = LastKnownDiscardForSeat(target, events);
            if (pai is null || !MjaiJson.TryParseTile(pai, out Tile candidate))
                continue;
            string? type = ClassifyOwnCall(consumed, candidate);
            if (type is null)
                continue;
            // In riichi a chi may only claim from the left player.
            if (type == "chi" && target != ((seat + 3) & 3))
                continue;
            if (match >= 0)
                return false;
            match = target;
            claimedTile = candidate;
            matchType = type;
            matchPai = pai;
        }

        if (match < 0 || matchType is null || matchPai is null)
            return false;

        var repaired = MjaiJson.Object(new { type = matchType, actor = seat });
        repaired["pai"] = matchPai;
        repaired["target"] = match;
        var consumedArray = new JsonArray();
        foreach (Tile tile in consumed)
            consumedArray.Add(MjaiJson.EncodeTile(tile));
        repaired["consumed"] = consumedArray;
        evt = repaired;
        LastOwnMeldRepair = repaired.ToJsonString();
        return true;
    }

    /// <summary>
    /// Last own meld event rebuilt from hand-delta plus river evidence because
    /// the visual decode failed validation. Diagnostic only.
    /// </summary>
    public string? LastOwnMeldRepair { get; private set; }

    /// <summary>
    /// The encoded pai of the most recent dahai/kakan the engine knows for
    /// <paramref name="seat"/>: first the batch being built (latest wins),
    /// then earlier batches recorded by <see cref="NoteBatchSent"/>.
    /// </summary>
    private string? LastKnownDiscardForSeat(int seat, List<JsonObject> batchEvents)
    {
        for (int i = batchEvents.Count - 1; i >= 0; i--)
        {
            JsonObject evt = batchEvents[i];
            if (evt["type"]?.GetValue<string>() is not ("dahai" or "kakan"))
                continue;
            if ((evt["actor"]?.GetValue<int>() ?? -1) != seat)
                continue;
            string pai = evt["pai"]?.GetValue<string>() ?? string.Empty;
            return string.IsNullOrEmpty(pai) || pai == "?" ? null : pai;
        }
        return seat is >= 0 and < 4 ? lastSentDahaiBySeat[seat] : null;
    }

    /// <summary>
    /// Classifies the call formed by our consumed tiles plus a claimed tile:
    /// "pon" (two identical + same kind), "daiminkan" (three identical + same
    /// kind) or "chi" (three consecutive suited numbers). Red fives count as
    /// their ordinary number.
    /// </summary>
    internal static string? ClassifyOwnCall(IReadOnlyList<Tile> consumed, Tile claimed)
    {
        static bool SameKind(Tile a, Tile b) => a.Suit == b.Suit && a.Number == b.Number
            && a.IsHonor == b.IsHonor && a.HonorNumber == b.HonorNumber;

        if (consumed.Count == 3)
        {
            return SameKind(consumed[0], consumed[1])
                && SameKind(consumed[1], consumed[2])
                && SameKind(consumed[0], claimed)
                ? "daiminkan"
                : null;
        }

        if (consumed.Count != 2)
            return null;

        if (SameKind(consumed[0], consumed[1]) && SameKind(consumed[0], claimed))
            return "pon";

        if (claimed.IsHonor || consumed[0].IsHonor || consumed[1].IsHonor)
            return null;
        if (consumed[0].Suit != claimed.Suit || consumed[1].Suit != claimed.Suit)
            return null;

        Span<int> numbers = [consumed[0].Number, consumed[1].Number, claimed.Number];
        numbers.Sort();
        return numbers[0] + 1 == numbers[1] && numbers[1] + 1 == numbers[2]
            ? "chi"
            : null;
    }

    /// <summary>Tiles present in <paramref name="before"/> but not in <paramref name="after"/> (multiset semantics).</summary>
    internal static List<Tile> MultisetDifference(IReadOnlyList<Tile> before, IReadOnlyList<Tile> after)
    {
        var remaining = new List<Tile>(before);
        foreach (Tile tile in after)
            remaining.Remove(tile);
        return remaining;
    }

    /// <summary>
    /// Last chi/pon/daiminkan event that was withheld because its claimed
    /// tile was never sent to the engine as a dahai. Diagnostic only; the
    /// caller may log it to correlate with field captures.
    /// </summary>
    public string? LastRejectedMeldEvent { get; private set; }

    /// <summary>
    /// True when the engine has already been told (in an earlier batch via
    /// <see cref="NoteBatchSent"/>, or earlier in the batch being built) that
    /// the meld's target actor discarded the claimed tile. A meld event that
    /// fails this check references a tile the engine never saw on the river
    /// and would poison its ordered session.
    /// </summary>
    private bool ClaimedDiscardIsKnownToEngine(JsonObject meldEvent, List<JsonObject> batchEvents)
    {
        int target = meldEvent["target"]?.GetValue<int>() ?? -1;
        string pai = meldEvent["pai"]?.GetValue<string>() ?? string.Empty;
        if (target < 0 || string.IsNullOrEmpty(pai) || pai == "?")
            return false;

        if (sentDahaiKeys.Contains($"{target}|{pai}"))
            return true;

        foreach (JsonObject evt in batchEvents)
        {
            if (evt["type"]?.GetValue<string>() is not ("dahai" or "kakan"))
                continue;
            if ((evt["actor"]?.GetValue<int>() ?? -1) != target)
                continue;
            if (string.Equals(evt["pai"]?.GetValue<string>(), pai, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string OwnMeldSignature(Meld meld)
    {
        string kind = meld.Kind switch
        {
            MeldKind.Chi => "chi",
            MeldKind.Pon => "pon",
            MeldKind.MinKan => "daiminkan",
            MeldKind.AnKan => "ankan",
            MeldKind.ShouMinKan => "kakan",
            _ => meld.Kind.ToString(),
        };
        var tiles = meld.Tiles.Select(tile => tile.Id).OrderBy(id => id).ToArray();
        return $"{kind}|{meld.ClaimedFromSeat}|{string.Join(',', tiles)}";
    }

    private static bool MeldListsEquivalent(IReadOnlyList<Meld> left, IReadOnlyList<Meld> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!MeldEquivalent(left[i], right[i]))
                return false;
        }
        return true;
    }

    private static bool MeldEquivalent(Meld left, Meld right)
    {
        if (left.Kind != right.Kind
            || left.ClaimedFromSeat != right.ClaimedFromSeat
            || left.ClaimedTile != right.ClaimedTile
            || left.Tiles.Length != right.Tiles.Length)
            return false;

        for (int i = 0; i < left.Tiles.Length; i++)
        {
            if (left.Tiles[i] != right.Tiles[i])
                return false;
        }
        return true;
    }

    private static IReadOnlyList<Meld> GetMelds(StateSnapshot state, int seat)
    {
        if (seat == ClampSeat(state.OurSeat) && state.OurMelds.Count > 0)
            return state.OurMelds;
        return seat < state.Seats.Count ? state.Seats[seat].Melds : [];
    }

    private static bool TryBuildMeldEvent(
        Meld meld,
        int actor,
        StateSnapshot oldState,
        StateSnapshot state,
        out JsonObject evt)
    {
        evt = null!;
        string type = meld.Kind switch
        {
            MeldKind.Chi => "chi",
            MeldKind.Pon => "pon",
            MeldKind.AnKan => "ankan",
            MeldKind.MinKan => "daiminkan",
            MeldKind.ShouMinKan => "kakan",
            _ => "none",
        };
        if (type == "none")
            return false;

        int seat = ClampSeat(actor);
        int target = ResolveMeldTarget(meld, seat, oldState, state);
        bool needsTarget = meld.Kind is MeldKind.Chi or MeldKind.Pon or MeldKind.MinKan;
        if (needsTarget && target < 0)
            return false;

        var obj = new JsonObject
        {
            ["type"] = type,
            ["actor"] = seat,
        };

        Tile? claimed = meld.ClaimedTile;
        if (meld.Kind is not MeldKind.AnKan)
        {
            obj["pai"] = MjaiJson.EncodeTile(claimed ?? meld.Tiles[0]);
            if (needsTarget)
                obj["target"] = ClampSeat(target);
        }

        var consumed = meld.Tiles.ToList();
        if (claimed is { } claimedTile && meld.Kind is MeldKind.Chi or MeldKind.Pon or MeldKind.MinKan)
        {
            int removeIndex = consumed.FindIndex(t => t == claimedTile);
            if (removeIndex >= 0)
                consumed.RemoveAt(removeIndex);
        }

        // The current EMJ meld tracker can identify a Pon and its claimed tile
        // before it has populated the full Tiles array. Reconstruct the consumed
        // tiles so the mjai event remains valid instead of abandoning Mortal for
        // the rest of the open hand.
        if (consumed.Count == 0 && claimed is { } fallbackTile)
        {
            consumed = meld.Kind switch
            {
                MeldKind.Pon => [fallbackTile, fallbackTile],
                MeldKind.MinKan => [fallbackTile, fallbackTile, fallbackTile],
                MeldKind.ShouMinKan => [fallbackTile, fallbackTile, fallbackTile],
                _ => consumed,
            };
        }
        if (meld.Kind == MeldKind.ShouMinKan && consumed.Count > 3)
            consumed = consumed.Take(3).ToList();

        obj["consumed"] = new JsonArray(consumed.Select(t => JsonValue.Create(MjaiJson.EncodeTile(t))).ToArray());
        evt = obj;
        return true;
    }

    /// <summary>
    /// Resolves mjai <c>target</c> (absolute seat that discarded the claimed tile).
    /// Chi is always from kamicha. Pon/MinKan prefer an explicit ClaimedFromSeat,
    /// then river loss / last matching discard. Returns -1 when still unknown.
    /// </summary>
    private static int ResolveMeldTarget(
        Meld meld,
        int actor,
        StateSnapshot oldState,
        StateSnapshot state)
    {
        if (meld.Kind is MeldKind.AnKan or MeldKind.ShouMinKan)
            return -1;

        if (meld.ClaimedFromSeat >= 0)
            return ClampSeat(meld.ClaimedFromSeat);

        // Chi can only claim the previous player's discard.
        if (meld.Kind == MeldKind.Chi)
            return (ClampSeat(actor) + 3) & 3;

        Tile claimed = meld.ClaimedTile ?? (meld.Tiles.Length > 0 ? meld.Tiles[0] : default);
        if (claimed == default)
            return -1;

        if (TryFindDiscarderOfTile(oldState, state, actor, claimed, out int fromSeat))
            return fromSeat;

        return -1;
    }

    private static bool TryFindDiscarderOfTile(
        StateSnapshot oldState,
        StateSnapshot state,
        int actor,
        Tile claimed,
        out int fromSeat)
    {
        fromSeat = -1;
        int actorSeat = ClampSeat(actor);
        int matchCount = 0;
        int matchedSeat = -1;

        for (int seat = 0; seat < 4; seat++)
        {
            if (seat == actorSeat)
                continue;

            bool lostClaimedTile = SeatLostTileFromRiver(oldState, state, seat, claimed);
            bool oldLastMatches = SeatLastDiscardEquals(oldState, seat, claimed);
            bool newLastMatches = SeatLastDiscardEquals(state, seat, claimed);
            if (!lostClaimedTile && !oldLastMatches && !newLastMatches)
                continue;

            matchCount++;
            matchedSeat = seat;
        }

        if (matchCount == 1)
        {
            fromSeat = matchedSeat;
            return true;
        }

        // Prefer the unique seat whose river lost the claimed tile this frame.
        matchCount = 0;
        matchedSeat = -1;
        for (int seat = 0; seat < 4; seat++)
        {
            if (seat == actorSeat)
                continue;
            if (!SeatLostTileFromRiver(oldState, state, seat, claimed))
                continue;
            matchCount++;
            matchedSeat = seat;
        }

        if (matchCount == 1)
        {
            fromSeat = matchedSeat;
            return true;
        }

        return false;
    }

    private static bool SeatLostTileFromRiver(
        StateSnapshot oldState,
        StateSnapshot state,
        int seat,
        Tile claimed)
    {
        if (seat >= oldState.Seats.Count || seat >= state.Seats.Count)
            return false;

        SeatView oldSeat = oldState.Seats[seat];
        SeatView newSeat = state.Seats[seat];
        int oldKindCount = CountTileKind(oldSeat.Discards, claimed);
        int newKindCount = CountTileKind(newSeat.Discards, claimed);
        if (newKindCount < oldKindCount)
            return true;

        // Some readers shrink DiscardCount when a call removes the tip tile,
        // even before the concrete Discards array updates.
        int oldCount = Math.Max(oldSeat.DiscardCount, oldSeat.Discards.Count);
        int newCount = Math.Max(newSeat.DiscardCount, newSeat.Discards.Count);
        return newCount < oldCount && SeatLastDiscardEquals(oldState, seat, claimed);
    }

    private static bool SeatLastDiscardEquals(StateSnapshot state, int seat, Tile claimed)
    {
        if (seat >= state.Seats.Count)
            return false;
        var discards = state.Seats[seat].Discards;
        return discards.Count > 0 && SameTileKind(discards[^1], claimed);
    }

    private static int CountTileKind(IReadOnlyList<Tile> tiles, Tile claimed)
    {
        int count = 0;
        for (int i = 0; i < tiles.Count; i++)
        {
            if (SameTileKind(tiles[i], claimed))
                count++;
        }
        return count;
    }

    private static bool SameTileKind(Tile left, Tile right)
        => left.Id == right.Id;

    private void AppendDiscardsAndRiichi(
        StateSnapshot oldState,
        StateSnapshot state,
        HashSet<int> calledSeats,
        List<JsonObject> events,
        HashSet<int>? withheldRiverSeats = null)
    {
        for (int seat = 0; seat < Math.Min(4, Math.Min(oldState.Seats.Count, state.Seats.Count)); seat++)
        {
            SeatView oldSeat = oldState.Seats[seat];
            SeatView newSeat = state.Seats[seat];
            int oldCount = Math.Max(oldSeat.DiscardCount, oldSeat.Discards.Count);
            int newCount = Math.Max(newSeat.DiscardCount, newSeat.Discards.Count);
            bool riichiTransition = !oldSeat.Riichi && newSeat.Riichi;
            bool reachEmitted = false;

            // The river reader can publish a seat's DiscardCount one frame before
            // the concrete tile array (field capture 2026-08-01 18:04: a frame
            // with counts 3/2/3 and riverCand=0). Emitting a partial range or
            // silently absorbing the count into `previous` loses those dahai
            // forever, because the delta detector is count-based. Withhold the
            // whole seat until every new discard resolves to a concrete tile;
            // the next decoded frame replays the range in order.
            if (seat != ClampSeat(state.OurSeat)
                && withheldRiverSeats is not null
                && HasUnresolvableDiscard(state, seat, oldCount, newCount))
            {
                withheldRiverSeats.Add(seat);
                continue;
            }

            for (int i = oldCount; i < newCount; i++)
            {
                bool isObservedReach = riichiTransition
                    && (newSeat.RiichiDiscardIndex < 0 || newSeat.RiichiDiscardIndex == i || i == newCount - 1);
                bool isPendingOwnReach = seat == ClampSeat(state.OurSeat)
                    && pendingRiichiDiscard is { } pending
                    && i < newSeat.Discards.Count
                    && newSeat.Discards[i] == pending;
                bool isReachDiscard = isObservedReach || isPendingOwnReach;

                Tile? observedDiscard = i < newSeat.Discards.Count
                    ? newSeat.Discards[i]
                    : null;

                // EMJ currently exposes discard counts without river tile IDs.
                // We can still echo our own chosen discard and the concrete tile
                // that produced an active call prompt. This is enough to preserve
                // Mortal's private hand and open-meld state across the hand.
                if (observedDiscard is null && seat == ClampSeat(state.OurSeat) && pendingOwnDiscard is { } ownDiscard)
                    observedDiscard = ownDiscard;
                if (observedDiscard is null
                    && TryGetCallOffer(state, out Tile offeredTile, out int offeredFromSeat)
                    && seat == ClampSeat(offeredFromSeat)
                    && i == newCount - 1)
                    observedDiscard = offeredTile;
                if (observedDiscard is null)
                    continue; // Never emit an unmatched opponent tsumo or dahai with pai="?".

                // An opponent draw is only useful when it is immediately paired
                // with a concrete discard. Sending tsumo("?") by itself advances
                // Mortal to a state where it correctly returns none, which the
                // caller previously mistook for a failed decision.
                if (seat != ClampSeat(state.OurSeat) && !calledSeats.Contains(seat))
                    events.Add(MjaiJson.Object(new { type = "tsumo", actor = seat, pai = "?" }));

                if (isReachDiscard)
                {
                    events.Add(MjaiJson.Object(new { type = "reach", actor = seat }));
                    reachEmitted = true;
                }

                string tile = MjaiJson.EncodeTile(observedDiscard.Value);
                bool tedashi = i < newSeat.DiscardIsTedashi.Count
                    ? newSeat.DiscardIsTedashi[i]
                    : seat == ClampSeat(state.OurSeat);
                events.Add(MjaiJson.Object(new
                {
                    type = "dahai",
                    actor = seat,
                    pai = tile,
                    tsumogiri = !tedashi,
                }));

                if (seat == ClampSeat(state.OurSeat) && pendingOwnDiscard == observedDiscard)
                    pendingOwnDiscard = null;
                if (seat == ClampSeat(state.OurSeat))
                {
                    ownDrawOutstanding = false;
                    ownDiscardAwaitingHandCommit = state.Hand.Count > 0
                        && state.Hand.Count % 3 == 2;
                }

                if (isReachDiscard)
                {
                    events.Add(MjaiJson.Object(new { type = "reach_accepted", actor = seat }));
                    if (isPendingOwnReach)
                        pendingRiichiDiscard = null;
                }
            }

            if (riichiTransition && !reachEmitted)
            {
                events.Add(MjaiJson.Object(new { type = "reach", actor = seat }));
                events.Add(MjaiJson.Object(new { type = "reach_accepted", actor = seat }));
            }
        }
    }

    /// <summary>
    /// True when any new discard of <paramref name="seat"/> in
    /// [<paramref name="fromCount"/>, <paramref name="toCount"/>) cannot be
    /// resolved to a concrete tile with the same rules the emission loop uses
    /// (river array, or the active call offer for the newest slot).
    /// </summary>
    private bool HasUnresolvableDiscard(StateSnapshot state, int seat, int fromCount, int toCount)
    {
        SeatView view = state.Seats[seat];
        for (int i = fromCount; i < toCount; i++)
        {
            if (i < view.Discards.Count && view.Discards[i].Id < Tile.Count34)
                continue;
            if (i == toCount - 1
                && TryGetCallOffer(state, out _, out int offeredFromSeat)
                && seat == ClampSeat(offeredFromSeat))
                continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// When the batch ends with an opponent's riichi dahai followed by its
    /// reach_accepted, holds the acceptance back for the next batch so the
    /// dahai stays the batch's decision boundary and Mortal is asked about
    /// calling the riichi tile. mjai order also requires the acceptance to
    /// follow the call window, not precede it.
    /// </summary>
    private void DeferTrailingReachAccepted(List<JsonObject> events, int ourSeat)
    {
        if (events.Count < 2)
            return;

        JsonObject last = events[^1];
        if (!string.Equals(
                last["type"]?.GetValue<string>(), "reach_accepted", StringComparison.Ordinal))
            return;

        int actor = last["actor"]?.GetValue<int>() ?? -1;
        if (actor < 0 || actor == ourSeat)
            return;

        JsonObject beforeLast = events[^2];
        if (!string.Equals(beforeLast["type"]?.GetValue<string>(), "dahai", StringComparison.Ordinal)
            || (beforeLast["actor"]?.GetValue<int>() ?? -1) != actor)
            return;

        events.RemoveAt(events.Count - 1);
        deferredReachAcceptedActor = actor;
    }

    private void AppendOwnDraw(
        StateSnapshot oldState,
        StateSnapshot state,
        HashSet<int> calledSeats,
        List<JsonObject> events)
    {
        int ourSeat = ClampSeat(state.OurSeat);
        if (!state.Legal.Can(ActionFlags.Discard)
            || state.Hand.Count == 0
            || state.Hand.Count % 3 != 2)
            return;

        bool normalDraw = !calledSeats.Contains(ourSeat)
            && state.Hand.Count == oldState.Hand.Count + 1;

        // EMJ often keeps the pre-discard 14-tile array visible in state 30.
        // At the next real turn the new hand is also 14 tiles, so a count-only
        // comparison misses the draw. Detect the one newly-added tile from the
        // hand multiset and use it as the tsumo event.
        Tile? replacementDraw = null;
        if (!normalDraw && !calledSeats.Contains(ourSeat)
            && state.Hand.Count == oldState.Hand.Count
            && TryFindAddedTile(oldState.Hand, state.Hand, out Tile addedTile))
        {
            normalDraw = true;
            replacementDraw = addedTile;
        }

        // Discarding tile X and then drawing another X leaves the hand multiset
        // unchanged, so TryFindAddedTile cannot see the new draw and the
        // hand-commit block never releases (the 13-tile frame fell into a poll
        // gap while the game waits for our discard: a deadlock). The wall is
        // authoritative here: two or more draws since the last synchronized
        // snapshot cannot happen within the same turn's stale-array transition,
        // so this actionable 14-tile hand is a real new turn (field capture
        // 2026-08-01 18:38: discard 8s, redraw 8s, events=0 forever).
        if (!normalDraw
            && !calledSeats.Contains(ourSeat)
            && ownDiscardAwaitingHandCommit
            && state.Hand.Count == oldState.Hand.Count
            && oldState.WallRemaining - state.WallRemaining >= 2)
        {
            normalDraw = true;
        }

        bool rinshanDraw = false;
        if (calledSeats.Contains(ourSeat))
        {
            var oldMelds = GetMelds(oldState, ourSeat);
            var newMelds = GetMelds(state, ourSeat);
            for (int i = 0; i < newMelds.Count; i++)
            {
                bool isNewOrChanged = i >= oldMelds.Count || !MeldEquivalent(oldMelds[i], newMelds[i]);
                if (isNewOrChanged && newMelds[i].IsKan)
                {
                    rinshanDraw = true;
                    break;
                }
            }
        }

        if (!normalDraw && !rinshanDraw)
            return;

        Tile drawTile = replacementDraw ?? state.Hand[^1];
        events.Add(MjaiJson.Object(new
        {
            type = "tsumo",
            actor = ourSeat,
            pai = MjaiJson.EncodeTile(drawTile),
        }));
        ownDiscardAwaitingHandCommit = false;
        ownDrawOutstanding = true;
    }

    private static bool TryFindAddedTile(IReadOnlyList<Tile> oldHand, IReadOnlyList<Tile> newHand, out Tile added)
    {
        Span<int> counts = stackalloc int[Tile.Count34];
        foreach (Tile tile in oldHand)
        {
            if ((uint)tile.Id < Tile.Count34)
                counts[tile.Id]++;
        }

        foreach (Tile tile in newHand)
        {
            if ((uint)tile.Id >= Tile.Count34)
                continue;
            counts[tile.Id]--;
            if (counts[tile.Id] < 0)
            {
                added = tile;
                return true;
            }
        }

        added = default;
        return false;
    }

    private static int InferDealer(StateSnapshot state)
    {
        int ourSeat = ClampSeat(state.OurSeat);
        int[] counts = new int[4];
        for (int seat = 0; seat < Math.Min(4, state.Seats.Count); seat++)
            counts[seat] = Math.Max(state.Seats[seat].DiscardCount, state.Seats[seat].Discards.Count);
        int total = counts.Sum();

        if (total == 0 && state.Hand.Count == 14)
            return ourSeat;

        if (total is > 0 and <= 3)
        {
            for (int candidate = 0; candidate < 4; candidate++)
            {
                bool matches = true;
                for (int offset = 0; offset < 4; offset++)
                {
                    int seat = (candidate + offset) % 4;
                    int expected = offset < total ? 1 : 0;
                    if (counts[seat] != expected)
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches)
                    return candidate;
            }
        }

        return ClampSeat(state.DealerSeat);
    }

    private static int ClampSeat(int seat) => Math.Clamp(seat, 0, 3);
}
