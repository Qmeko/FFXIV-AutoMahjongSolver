using System.Reflection;
using System.Text.Json.Nodes;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Mahjong.Policy.Abstractions;
using Mahjong.Plugin.Dalamud.Tests.Stubs;

namespace Mahjong.Plugin.Dalamud.Tests;

public class ExternalAiProtocolTests
{
    [Fact]
    public void Akochan_pending_choice_is_refreshed_like_mortal_pending()
    {
        var pending = ActionChoice.Pass("Akochan pending: calculating this decision");

        Assert.True(SelectablePolicy.IsPendingChoice(pending));
    }

    [Theory]
    [InlineData("Akochan unavailable: process ended")]
    [InlineData("selected AI unavailable: process ended")]
    [InlineData("選択AI専用モード")]
    public void Unavailable_selected_ai_sentinel_is_not_rendered_as_pass_advice(string reasoning)
    {
        Assert.True(SelectablePolicy.IsPendingChoice(ActionChoice.Pass(reasoning)));
    }

    [Fact]
    public void Stale_precommit_hand_after_own_discard_never_emits_a_duplicate_self_draw()
    {
        Tile[] hand14 = Enumerable.Range(0, 14)
            .Select(i => Tile.FromId(i))
            .ToArray();
        Tile discarded = hand14[3];
        Tile[] hand13 = hand14.Where((_, index) => index != 3).ToArray();
        Tile nextDraw = Tile.FromId(20);

        var firstDraw = StateSnapshot.Empty with
        {
            Hand = hand14,
            WallRemaining = 68,
            DoraIndicators = [Tile.FromId(19)],
            Legal = new LegalActions(ActionFlags.Discard, hand14, [], [], []),
            AddonStateCode = 6,
        };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(firstDraw);
        tracker.NoteChoice(ActionChoice.Discard(discarded), firstDraw);

        SeatView[] staleSeats = firstDraw.Seats.ToArray();
        staleSeats[0] = staleSeats[0] with
        {
            Discards = [discarded],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var stalePreCommit = firstDraw with
        {
            Seats = staleSeats,
            WallRemaining = 67,
            // EMJ has already published the river entry but still exposes the
            // old 14-tile hand and Discard surface.
            Hand = hand14,
            Legal = new LegalActions(ActionFlags.Discard, hand14, [], [], []),
            AddonStateCode = 30,
        };

        MjaiEventBatch discardBatch = tracker.BuildBatch(stalePreCommit);
        JsonArray discardEvents = JsonNode.Parse(discardBatch.Json)!.AsArray();
        Assert.Single(discardEvents);
        Assert.Equal("dahai", discardEvents[0]!["type"]!.GetValue<string>());
        Assert.False(ExternalMjaiProcess.BatchExpectsDecision(discardBatch.Json, ourSeat: 0));

        MjaiEventBatch repeatedStale = tracker.BuildBatch(stalePreCommit);
        Assert.Equal(0, repeatedStale.EventCount);

        var committed = stalePreCommit with
        {
            Hand = hand13,
            Legal = LegalActions.None,
            AddonStateCode = 15,
        };
        Assert.Equal(0, tracker.BuildBatch(committed).EventCount);

        Tile[] nextHand14 = hand13.Append(nextDraw).ToArray();
        var nextActionableDraw = committed with
        {
            Hand = nextHand14,
            WallRemaining = 64,
            Legal = new LegalActions(ActionFlags.Discard, nextHand14, [], [], []),
            AddonStateCode = 6,
        };
        MjaiEventBatch nextDrawBatch = tracker.BuildBatch(nextActionableDraw);
        JsonArray nextDrawEvents = JsonNode.Parse(nextDrawBatch.Json)!.AsArray();

        Assert.Single(nextDrawEvents);
        Assert.Equal("tsumo", nextDrawEvents[0]!["type"]!.GetValue<string>());
        Assert.Equal(MjaiJson.EncodeTile(nextDraw), nextDrawEvents[0]!["pai"]!.GetValue<string>());
        Assert.True(ExternalMjaiProcess.BatchExpectsDecision(nextDrawBatch.Json, ourSeat: 0));
    }

    [Fact]
    public void Fresh_self_draw_is_sent_as_one_mjai_array_batch()
    {
        var hand = Enumerable.Range(0, 14).Select(i => Tile.FromId(i % Tile.Count34)).ToArray();
        var state = StateSnapshot.Empty with
        {
            Hand = hand,
            WallRemaining = 70,
            DoraIndicators = [Tile.FromId(12)],
            Legal = new LegalActions(ActionFlags.Discard, hand, [], [], []),
            AddonStateCode = 6,
        };

        var tracker = new MjaiSessionTracker();
        var batch = tracker.BuildBatch(state);
        var events = JsonNode.Parse(batch.Json)!.AsArray();

        Assert.True(batch.StartsGame);
        Assert.Equal(3, events.Count);
        Assert.Equal("start_game", events[0]!["type"]!.GetValue<string>());
        Assert.Equal("start_kyoku", events[1]!["type"]!.GetValue<string>());
        Assert.Equal("tsumo", events[2]!["type"]!.GetValue<string>());
        Assert.Equal(0, events[2]!["actor"]!.GetValue<int>());
    }

    [Fact]
    public void Kakan_response_matches_the_added_tile_even_when_consumed_lists_the_old_pon()
    {
        Tile fiveMan = Tile.FromId(4);
        var candidate = new MeldCandidate(
            MeldKind.ShouMinKan,
            fiveMan,
            [fiveMan],
            1);
        var state = StateSnapshot.Empty with
        {
            Hand = [fiveMan],
            Legal = new LegalActions(
                ActionFlags.ShouMinKan | ActionFlags.Pass,
                [],
                [],
                [],
                [candidate]),
        };
        JsonObject response = JsonNode.Parse(
            """{"type":"kakan","actor":0,"pai":"5m","consumed":["5m","5m","5m"]}""")!.AsObject();

        bool mapped = MjaiActionMapper.TryMap(response, state, ActionChoice.Pass(), out var choice);

        Assert.True(mapped);
        Assert.Equal(ActionKind.ShouMinKan, choice.Kind);
        Assert.Equal(candidate, choice.Call);
    }

    [Fact]
    public void Chi_response_selects_the_exact_consumed_pair()
    {
        Tile claim = Tile.FromId(2); // 3m
        Tile one = Tile.FromId(0);
        Tile two = Tile.FromId(1);
        Tile four = Tile.FromId(3);
        Tile five = Tile.FromId(4);
        var low = new MeldCandidate(MeldKind.Chi, claim, [one, two], 3);
        var high = new MeldCandidate(MeldKind.Chi, claim, [four, five], 3);
        var state = StateSnapshot.Empty with
        {
            Hand = [one, two, four, five],
            Legal = new LegalActions(
                ActionFlags.Chi | ActionFlags.Pass,
                [],
                [],
                [low, high],
                []),
        };
        JsonObject response = JsonNode.Parse(
            """{"type":"chi","actor":0,"target":3,"pai":"3m","consumed":["4m","5m"]}""")!.AsObject();

        bool mapped = MjaiActionMapper.TryMap(response, state, ActionChoice.Pass(), out var choice);

        Assert.True(mapped);
        Assert.Equal(ActionKind.Chi, choice.Kind);
        Assert.Equal(high, choice.Call);
    }

    [Fact]
    public void Chi_response_maps_its_atomic_follow_up_discard()
    {
        Tile claim = Tile.FromId(15); // 7p
        Tile six = Tile.FromId(14);
        Tile eight = Tile.FromId(16);
        Tile oneMan = Tile.FromId(0);
        var candidate = new MeldCandidate(MeldKind.Chi, claim, [six, eight], 3);
        var state = StateSnapshot.Empty with
        {
            Hand = [oneMan, six, eight, Tile.FromId(4)],
            Legal = new LegalActions(
                ActionFlags.Chi | ActionFlags.Pass,
                [],
                [],
                [candidate],
                []),
        };
        JsonObject response = JsonNode.Parse(
            """{"type":"chi","actor":0,"target":3,"pai":"7p","consumed":["6p","8p"],"_post_call_pai":"1m"}""")!.AsObject();

        bool mapped = MjaiActionMapper.TryMap(response, state, ActionChoice.Pass(), out var choice);

        Assert.True(mapped);
        Assert.Equal(ActionKind.Chi, choice.Kind);
        Assert.Equal(oneMan, choice.PostCallDiscardTile!.Value);
    }

    [Fact]
    public void Atomic_follow_up_discard_cannot_reuse_a_consumed_tile()
    {
        Tile claim = Tile.FromId(15); // 7p
        Tile six = Tile.FromId(14);
        Tile eight = Tile.FromId(16);
        var candidate = new MeldCandidate(MeldKind.Chi, claim, [six, eight], 3);
        var state = StateSnapshot.Empty with
        {
            Hand = [six, eight, Tile.FromId(4)],
            Legal = new LegalActions(
                ActionFlags.Chi | ActionFlags.Pass,
                [],
                [],
                [candidate],
                []),
        };
        JsonObject response = JsonNode.Parse(
            """{"type":"chi","actor":0,"target":3,"pai":"7p","consumed":["6p","8p"],"_post_call_pai":"6p"}""")!.AsObject();

        bool mapped = MjaiActionMapper.TryMap(response, state, ActionChoice.Pass(), out var choice);

        Assert.True(mapped);
        Assert.Null(choice.PostCallDiscardTile);
    }

    [Fact]
    public void Illegal_external_discard_is_rejected()
    {
        Tile legal = Tile.FromId(0);
        var state = StateSnapshot.Empty with
        {
            Hand = [legal],
            Legal = new LegalActions(ActionFlags.Discard, [legal], [], [], []),
        };
        JsonObject response = JsonNode.Parse(
            """{"type":"dahai","actor":0,"pai":"9s","tsumogiri":false}""")!.AsObject();

        bool mapped = MjaiActionMapper.TryMap(response, state, ActionChoice.Discard(legal), out _);

        Assert.False(mapped);
    }
    [Fact]
    public void Authoritative_call_prompt_repair_preserves_ordered_batch_and_changes_only_offer_tile()
    {
        Tile offered = Tile.FromId(33);        // red dragon
        var hand = new[]
        {
            Tile.FromId(1), Tile.FromId(9), Tile.FromId(10), Tile.FromId(11), Tile.FromId(11),
            Tile.FromId(20), Tile.FromId(21), Tile.FromId(22), Tile.FromId(28), Tile.FromId(28),
            Tile.FromId(31), offered, offered,
        };
        var candidate = new MeldCandidate(MeldKind.Pon, offered, [offered, offered], FromSeat: 3);
        var state = StateSnapshot.Empty with
        {
            Hand = hand,
            WallRemaining = 64,
            DoraIndicators = [Tile.FromId(20)],
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Pass,
                [],
                [candidate],
                [],
                []),
            AddonStateCode = 15,
        };
        JsonArray originalEvents = JsonNode.Parse(
            """[{"type":"start_game","id":0},{"type":"start_kyoku"},{"type":"tsumo","actor":0,"pai":"4m"},{"type":"dahai","actor":0,"pai":"4m"},{"type":"tsumo","actor":3,"pai":"?"},{"type":"dahai","actor":3,"pai":"3p","tsumogiri":false}]""")!.AsArray();
        var source = new MjaiEventBatch(originalEvents.ToJsonString(), originalEvents.Count, true, "test");

        var tracker = new MjaiSessionTracker();
        bool built = tracker.TryCorrectAuthoritativeCallPromptBatch(
            state,
            source,
            out var corrected,
            out string sourceKey,
            out string correctedKey);
        JsonArray events = JsonNode.Parse(corrected.Json)!.AsArray();

        Assert.True(built);
        Assert.Equal(source.EventCount, corrected.EventCount);
        Assert.Equal("3|3p", sourceKey);
        Assert.Equal("3|C", correctedKey);
        Assert.Equal("start_game", events[0]!["type"]!.GetValue<string>());
        Assert.Equal("4m", events[3]!["pai"]!.GetValue<string>());
        Assert.Equal(3, events[^1]!["actor"]!.GetValue<int>());
        Assert.Equal("C", events[^1]!["pai"]!.GetValue<string>());
        Assert.Equal("3p", originalEvents[^1]!["pai"]!.GetValue<string>());
    }

    [Fact]
    public void Authoritative_call_prompt_repair_rejects_conflicting_offer_candidates()
    {
        Tile red = Tile.FromId(33);
        Tile white = Tile.FromId(31);
        var hand = Enumerable.Repeat(red, 13).ToArray();
        var state = StateSnapshot.Empty with
        {
            Hand = hand,
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Pass,
                [],
                [
                    new MeldCandidate(MeldKind.Pon, red, [red, red], FromSeat: 1),
                    new MeldCandidate(MeldKind.Pon, white, [white, white], FromSeat: 1),
                ],
                [],
                []),
        };
        var source = new MjaiEventBatch(
            """[{"type":"tsumo","actor":3,"pai":"?"},{"type":"dahai","actor":3,"pai":"C"}]""",
            2,
            false,
            "test");

        var tracker = new MjaiSessionTracker();
        bool built = tracker.TryCorrectAuthoritativeCallPromptBatch(
            state,
            source,
            out _,
            out _,
            out _);

        Assert.False(built);
    }

    [Fact]
    public void Authoritative_chi_repair_requires_ordered_batch_actor_to_be_absolute_kamicha()
    {
        Tile offered = Tile.FromId(11); // 3p
        Tile onePin = Tile.FromId(9);
        Tile twoPin = Tile.FromId(10);
        var state = StateSnapshot.Empty with
        {
            OurSeat = 2,
            Hand = Enumerable.Repeat(Tile.FromId(0), 13).ToArray(),
            Legal = new LegalActions(
                ActionFlags.Chi | ActionFlags.Pass,
                [],
                [],
                [new MeldCandidate(MeldKind.Chi, offered, [onePin, twoPin], FromSeat: 3)],
                []),
        };
        var wrongActor = new MjaiEventBatch(
            """[{"type":"tsumo","actor":3,"pai":"?"},{"type":"dahai","actor":3,"pai":"2p"}]""",
            2,
            false,
            "test");
        var correctActor = new MjaiEventBatch(
            """[{"type":"tsumo","actor":1,"pai":"?"},{"type":"dahai","actor":1,"pai":"2p"}]""",
            2,
            false,
            "test");

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(state with { Legal = LegalActions.None });
        Assert.True(tracker.TryCorrectAuthoritativeCallPromptBatch(
            state, wrongActor, out var appended, out _, out string appendKey));
        Assert.Equal("1|3p", appendKey);
        Assert.True(tracker.TryCorrectAuthoritativeCallPromptBatch(
            state, correctActor, out var repaired, out _, out string key));
        Assert.Equal("1|3p", key);
        Assert.Equal("3p", JsonNode.Parse(repaired.Json)!.AsArray()[^1]!["pai"]!.GetValue<string>());
    }

    [Fact]
    public void Chi_response_maps_absolute_target_for_non_east_player()
    {
        Tile claim = Tile.FromId(2); // 3m
        Tile one = Tile.FromId(0);
        Tile two = Tile.FromId(1);
        var candidate = new MeldCandidate(MeldKind.Chi, claim, [one, two], FromSeat: 3);
        var state = StateSnapshot.Empty with
        {
            OurSeat = 2,
            Hand = [one, two],
            Legal = new LegalActions(
                ActionFlags.Chi | ActionFlags.Pass,
                [],
                [],
                [candidate],
                []),
        };
        JsonObject response = JsonNode.Parse(
            """{"type":"chi","actor":2,"target":1,"pai":"3m","consumed":["1m","2m"]}""")!.AsObject();

        bool mapped = MjaiActionMapper.TryMap(response, state, ActionChoice.Pass(), out var choice);

        Assert.True(mapped);
        Assert.Equal(candidate, choice.Call);
    }

    [Fact]
    public void Pon_response_preserves_exact_red_consumed_pattern()
    {
        Tile fiveMan = Tile.FromId(4);
        var candidate = new MeldCandidate(MeldKind.Pon, fiveMan, [fiveMan, fiveMan], FromSeat: 1);
        var state = StateSnapshot.Empty with
        {
            Hand = [fiveMan, fiveMan],
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Pass,
                [],
                [candidate],
                [],
                []),
        };
        JsonObject response = JsonNode.Parse(
            """{"type":"pon","actor":0,"target":1,"pai":"5m","consumed":["0m","5m"]}""")!.AsObject();

        bool mapped = MjaiActionMapper.TryMap(response, state, ActionChoice.Pass(), out var choice);

        Assert.True(mapped);
        Assert.Equal(ActionKind.Pon, choice.Kind);
        Assert.Equal(candidate, choice.Call);
        Assert.Equal(new[] { true, false }, choice.CallConsumedRed);
    }

    [Fact]
    public void Akochan_pon_uses_unique_structural_candidate_when_emj_from_seat_is_wrong()
    {
        Tile north = Tile.FromId(30);
        var candidate = new MeldCandidate(MeldKind.Pon, north, [north, north], FromSeat: 1);
        var state = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = [north, north],
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Pass,
                [],
                [candidate],
                [],
                []),
        };
        JsonObject response = JsonNode.Parse(
            """{"type":"pon","actor":0,"target":3,"pai":"N","consumed":["N","N"]}""")!.AsObject();

        Assert.False(MjaiActionMapper.TryMap(
            response, state, ActionChoice.Pass(), out _, "Akochan"));

        bool mapped = MjaiActionMapper.TryMap(
            response, state, ActionChoice.Pass(), out var choice, "Akochan",
            allowUnreliableCallTarget: true);

        Assert.True(mapped);
        Assert.Equal(ActionKind.Pon, choice.Kind);
        Assert.NotNull(choice.Call);
        Assert.Equal(north, choice.Call.Value.ClaimedTile);
        Assert.Equal(new[] { north, north }, choice.Call.Value.HandTiles);
        Assert.Equal(3, choice.Call.Value.FromSeat);
        Assert.Contains("EMJ座席補正", choice.Reasoning);
    }

    [Fact]
    public void Akochan_target_relaxation_does_not_guess_between_structurally_different_calls()
    {
        Tile claim = Tile.FromId(2);
        Tile one = Tile.FromId(0);
        Tile two = Tile.FromId(1);
        Tile four = Tile.FromId(3);
        Tile five = Tile.FromId(4);
        var state = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = [one, two, four, five],
            Legal = new LegalActions(
                ActionFlags.Chi | ActionFlags.Pass,
                [],
                [],
                [
                    new MeldCandidate(MeldKind.Chi, claim, [one, two], FromSeat: 1),
                    new MeldCandidate(MeldKind.Chi, claim, [four, five], FromSeat: 1),
                ],
                []),
        };
        JsonObject response = JsonNode.Parse(
            """{"type":"chi","actor":0,"target":3,"pai":"3m"}""")!.AsObject();

        bool mapped = MjaiActionMapper.TryMap(
            response, state, ActionChoice.Pass(), out _, "Akochan",
            allowUnreliableCallTarget: true);

        Assert.False(mapped);
    }

    [Fact]
    public void Mixed_self_turn_flags_are_not_treated_as_an_external_call_prompt()
    {
        Tile claim = Tile.FromId(8);
        var state = StateSnapshot.Empty with
        {
            Hand = Enumerable.Range(0, 14).Select(i => Tile.FromId(i % Tile.Count34)).ToArray(),
            Legal = new LegalActions(
                ActionFlags.Discard | ActionFlags.Chi | ActionFlags.Pass,
                [claim],
                [],
                [new MeldCandidate(MeldKind.Chi, claim, [Tile.FromId(6), Tile.FromId(7)], FromSeat: 3)],
                []),
        };

        Assert.False(ExternalMjaiProcess.IsLiveExternalCallPrompt(state));
    }

    [Fact]
    public void Stable_thirteen_tile_offer_is_an_external_call_prompt()
    {
        Tile claim = Tile.FromId(8);
        var state = StateSnapshot.Empty with
        {
            Hand = Enumerable.Range(0, 13).Select(i => Tile.FromId(i % Tile.Count34)).ToArray(),
            Legal = new LegalActions(
                ActionFlags.Chi | ActionFlags.Pass,
                [],
                [],
                [new MeldCandidate(MeldKind.Chi, claim, [Tile.FromId(6), Tile.FromId(7)], FromSeat: 3)],
                []),
        };

        Assert.True(ExternalMjaiProcess.IsLiveExternalCallPrompt(state));
    }

    [Fact]
    public void Ron_and_tsumo_prompts_are_urgent_external_prompts()
    {
        var ronState = StateSnapshot.Empty with
        {
            Hand = Enumerable.Range(0, 13).Select(i => Tile.FromId(i % Tile.Count34)).ToArray(),
            Legal = new LegalActions(ActionFlags.Ron | ActionFlags.Pass, [], [], [], []),
        };
        var tsumoState = StateSnapshot.Empty with
        {
            Hand = Enumerable.Range(0, 14).Select(i => Tile.FromId(i % Tile.Count34)).ToArray(),
            Legal = new LegalActions(ActionFlags.Tsumo, [], [], [], []),
        };

        Assert.True(ExternalMjaiProcess.IsUrgentExternalPrompt(ronState));
        Assert.True(ExternalMjaiProcess.IsUrgentExternalPrompt(tsumoState));
    }

    [Fact]
    public void Hora_response_is_deferred_until_ron_or_tsumo_flag_appears()
    {
        var ronPending = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = Enumerable.Range(0, 13).Select(i => Tile.FromId(i % Tile.Count34)).ToArray(),
            Legal = new LegalActions(ActionFlags.Pass, [], [], [], []),
        };
        JsonObject ronResponse = JsonNode.Parse("""{"type":"hora","actor":0,"target":3}""")!.AsObject();
        JsonObject tsumoResponse = JsonNode.Parse("""{"type":"hora","actor":0,"target":0}""")!.AsObject();

        Assert.True(ExternalMjaiProcess.ShouldDeferTransientLegalSurface(
            ExternalEngineKind.Primary, true, ronResponse, ronPending));
        Assert.True(ExternalMjaiProcess.ShouldDeferTransientLegalSurface(
            ExternalEngineKind.Primary, true, tsumoResponse, ronPending with
            {
                Hand = Enumerable.Range(0, 14).Select(i => Tile.FromId(i % Tile.Count34)).ToArray(),
            }));

        var ronReady = ronPending with
        {
            Legal = new LegalActions(ActionFlags.Ron | ActionFlags.Pass, [], [], [], []),
        };
        Assert.False(ExternalMjaiProcess.ShouldDeferTransientLegalSurface(
            ExternalEngineKind.Primary, true, ronResponse, ronReady));
    }

    [Fact]
    public void Pending_retained_choice_keeps_last_recommendation_during_background_work()
    {
        Tile[] hand = Enumerable.Range(0, 14).Select(i => Tile.FromId(i % Tile.Count34)).ToArray();
        Tile discard = hand[0];
        var state = StateSnapshot.Empty with
        {
            Hand = hand,
            Legal = new LegalActions(ActionFlags.Discard, hand, [], [], []),
        };
        var retained = ActionChoice.Discard(discard, "Mortal: discard");

        using var process = new ExternalMjaiProcess(new StubPluginLog(), string.Empty);
        SetPrivateField(process, "lastChoice", retained);
        SetPrivateField(process, "lastFingerprint", ExternalMjaiProcess.Fingerprint(state));
        SetPrivateField(process, "lastPositionFingerprint", ExternalMjaiProcess.PositionFingerprint(state));
        SetPrivateField(process, "pendingFingerprint", ExternalMjaiProcess.Fingerprint(state));
        SetPrivateField(process, "backgroundTask", Task.Run(async () => await Task.Delay(500)));

        Assert.True(process.TryGetPendingRetainedChoice(state, out ActionChoice pending));
        Assert.Equal(retained, pending);
    }

    [Fact]
    public void Live_own_kan_prompt_is_detected_for_shou_minkan()
    {
        Tile twoZ = Tile.FromId(31);
        var state = StateSnapshot.Empty with
        {
            Hand = Enumerable.Range(0, 11).Select(i => Tile.FromId(i % Tile.Count34)).ToArray(),
            Legal = new LegalActions(
                ActionFlags.ShouMinKan | ActionFlags.Pass,
                [],
                [],
                [],
                [new MeldCandidate(MeldKind.ShouMinKan, twoZ, [twoZ], 1)]),
        };

        Assert.True(ExternalMjaiProcess.IsLiveOwnKanPrompt(state));
        Assert.True(ExternalMjaiProcess.IsUrgentExternalPrompt(state));
        Assert.False(ExternalMjaiProcess.IsLiveExternalCallPrompt(state));
    }

    [Fact]
    public void ShouMinKan_prompt_restores_missing_own_draw_event()
    {
        var hand13 = Enumerable.Range(0, 13).Select(i => Tile.FromId(i % Tile.Count34)).ToArray();
        var bootstrap = StateSnapshot.Empty with
        {
            Hand = hand13,
            WallRemaining = 70,
            DoraIndicators = [Tile.FromId(12)],
            Legal = new LegalActions(ActionFlags.Discard, hand13, [], [], []),
        };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(bootstrap);

        Tile twoZ = Tile.FromId(31);
        Tile[] hand10 = Enumerable.Range(0, 10).Select(i => Tile.FromId(i % Tile.Count34)).ToArray();
        var postDiscard = bootstrap with
        {
            Hand = hand10,
            WallRemaining = 65,
            Legal = new LegalActions(ActionFlags.Discard, hand10, [], [], []),
            OurMelds = [Meld.Pon(twoZ, twoZ, 1)],
        };
        _ = tracker.BuildBatch(postDiscard);
        tracker.NoteChoice(ActionChoice.Discard(hand10[0]), postDiscard);

        var stale = postDiscard with { Legal = LegalActions.None };
        _ = tracker.BuildBatch(stale);

        Tile[] hand11 = hand10.Append(twoZ).ToArray();
        var kanPrompt = stale with
        {
            Hand = hand11,
            Legal = new LegalActions(
                ActionFlags.ShouMinKan | ActionFlags.Pass,
                [],
                [],
                [],
                [new MeldCandidate(MeldKind.ShouMinKan, twoZ, [twoZ], 1)]),
        };

        MjaiEventBatch batch = tracker.BuildBatch(kanPrompt);
        JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();

        Assert.NotEmpty(events);
        Assert.Equal("tsumo", events[^1]!["type"]!.GetValue<string>());
        Assert.Equal(MjaiJson.EncodeTile(twoZ), events[^1]!["pai"]!.GetValue<string>());
        Assert.True(ExternalMjaiProcess.BatchExpectsDecision(batch.Json, ourSeat: 0));
    }

    [Fact]
    public void Rule_authoritative_chi_offer_uses_kamicha_river_when_candidate_rows_are_empty()
    {
        Tile fourMan = Tile.FromId(3);
        Tile fiveMan = Tile.FromId(4);
        Tile sixMan = Tile.FromId(5);
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = [sixMan],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var state = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = [fourMan, fiveMan, Tile.FromId(8)],
            Seats = seats,
            Legal = new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [], []),
            AddonStateCode = 15,
        };

        bool resolved = ExternalMjaiProcess.TryResolveRuleAuthoritativeChiOfferKey(state, out string key);

        Assert.True(resolved);
        Assert.Equal("3|6m", key);
    }

    [Fact]
    public void Rule_authoritative_chi_offer_does_not_use_a_non_kamicha_discard()
    {
        Tile fourMan = Tile.FromId(3);
        Tile fiveMan = Tile.FromId(4);
        Tile sixMan = Tile.FromId(5);
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[1] = seats[1] with
        {
            Discards = [sixMan],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var state = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = [fourMan, fiveMan, Tile.FromId(8)],
            Seats = seats,
            Legal = new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [], []),
            AddonStateCode = 15,
        };

        Assert.False(ExternalMjaiProcess.TryResolveRuleAuthoritativeChiOfferKey(state, out _));
    }

    [Fact]
    public void Akochan_chi_response_reconstructs_candidate_before_emj_rows_arrive()
    {
        Tile twoMan = Tile.FromId(1);
        Tile threeMan = Tile.FromId(2);
        Tile fourMan = Tile.FromId(3);
        var state = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = [threeMan, fourMan, Tile.FromId(8)],
            Legal = new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [], []),
            AddonStateCode = 15,
        };
        JsonObject response = JsonNode.Parse(
            """{"type":"chi","actor":0,"target":3,"pai":"2m","consumed":["3m","4m"]}""")!.AsObject();

        bool mapped = MjaiActionMapper.TryMap(
            response, state, ActionChoice.Pass(), out ActionChoice choice, "Akochan",
            allowUnreliableCallTarget: true);

        Assert.True(mapped);
        Assert.Equal(ActionKind.Chi, choice.Kind);
        Assert.NotNull(choice.Call);
        Assert.Equal(twoMan, choice.Call!.Value.ClaimedTile);
        Assert.Equal(new[] { threeMan, fourMan }, choice.Call.Value.HandTiles.ToArray());
        Assert.Contains("候補復元", choice.Reasoning);
    }

    [Fact]
    public void Zero_event_call_offer_is_appended_to_the_existing_ordered_session()
    {
        Tile claim = Tile.FromId(31); // white dragon
        var initial = StateSnapshot.Empty with
        {
            OurSeat = 2,
            Hand = Enumerable.Range(0, 13).Select(i => Tile.FromId(i % Tile.Count34)).ToArray(),
            DoraIndicators = [Tile.FromId(4)],
            Legal = LegalActions.None,
        };
        var call = initial with
        {
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Pass,
                [],
                [new MeldCandidate(MeldKind.Pon, claim, [claim, claim], FromSeat: 1)],
                [],
                []),
            AddonStateCode = 15,
        };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(initial);
        var empty = new MjaiEventBatch("[]", 0, false, "no public river delta yet");

        bool repaired = tracker.TryCorrectAuthoritativeCallPromptBatch(
            call, empty, out var batch, out string sourceKey, out string correctedKey);
        JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();

        Assert.True(repaired);
        Assert.Equal(string.Empty, sourceKey);
        Assert.Equal("3|P", correctedKey);
        Assert.Equal(2, events.Count);
        Assert.Equal("tsumo", events[0]!["type"]!.GetValue<string>());
        Assert.Equal(3, events[0]!["actor"]!.GetValue<int>());
        Assert.Equal("dahai", events[1]!["type"]!.GetValue<string>());
        Assert.Equal(3, events[1]!["actor"]!.GetValue<int>());
        Assert.Equal("P", events[1]!["pai"]!.GetValue<string>());
    }

    [Fact]
    public void Call_offer_append_rejects_duplicate_resend_of_same_actor_tile()
    {
        Tile claim = Tile.FromId(31); // white dragon
        var initial = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = Enumerable.Repeat(claim, 13).ToArray(),
            DoraIndicators = [Tile.FromId(4)],
            Legal = LegalActions.None,
        };
        SeatView[] seats = initial.Seats.ToArray();
        seats[2] = seats[2] with
        {
            Discards = [claim],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var call = initial with
        {
            Seats = seats,
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Pass,
                [],
                [new MeldCandidate(MeldKind.Pon, claim, [claim, claim], FromSeat: 2)],
                [],
                []),
            AddonStateCode = 15,
        };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(initial);

        Assert.True(tracker.TryAppendAuthoritativeCallPromptBatch(
            call, 2, claim, out MjaiEventBatch appendedBatch, out string firstKey));
        Assert.Equal("2|P", firstKey);

        // The offer is only "already synchronized" once the appended batch has
        // actually been written to the engine.
        Assert.False(tracker.AlreadyHasCallOffer(2, claim));
        tracker.NoteBatchSent(appendedBatch.Json);

        Assert.True(tracker.AlreadyHasCallOffer(2, claim));
        Assert.False(tracker.TryAppendAuthoritativeCallPromptBatch(
            call, 2, claim, out _, out _));
    }

    [Fact]
    public void Call_offer_append_rejects_tile_that_conflicts_with_public_river()
    {
        Tile white = Tile.FromId(31); // P
        Tile red = Tile.FromId(33);   // C
        var initial = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = Enumerable.Repeat(white, 13).ToArray(),
            DoraIndicators = [Tile.FromId(4)],
            Legal = LegalActions.None,
        };
        SeatView[] seats = initial.Seats.ToArray();
        seats[1] = seats[1] with
        {
            Discards = [red],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var call = initial with
        {
            Seats = seats,
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Pass,
                [],
                // Stale ClaimedTile still names white while river tip is red.
                [new MeldCandidate(MeldKind.Pon, white, [white, white], FromSeat: 1)],
                [],
                []),
            AddonStateCode = 15,
        };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(initial);

        Assert.True(MjaiSessionTracker.CallOfferConflictsWithRiver(call, 1, white));
        Assert.False(tracker.TryAppendAuthoritativeCallPromptBatch(
            call, 1, white, out _, out _));
        Assert.False(tracker.TryCorrectAuthoritativeCallPromptBatch(
            call,
            new MjaiEventBatch("[]", 0, false, "empty"),
            1,
            white,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void Unique_callable_river_offer_prefers_river_tip_over_stale_candidate()
    {
        Tile white = Tile.FromId(31);
        Tile red = Tile.FromId(33);
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[2] = seats[2] with
        {
            Discards = [red],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var state = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = [white, white, .. Enumerable.Range(0, 11).Select(i => Tile.FromId(i))],
            Seats = seats,
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Pass,
                [],
                [new MeldCandidate(MeldKind.Pon, white, [white, white], FromSeat: 1)],
                [],
                []),
        };

        // River tip is red at seat 2 and is not callable (hand has white pairs).
        // Stale candidate white@seat1 conflicts with an empty/mismatched river and
        // must not win; no unique callable river tip either.
        Assert.False(ExternalMjaiProcess.TryGetUniqueCallableRiverOfferKey(state, out _));

        seats = state.Seats.ToArray();
        seats[1] = seats[1] with
        {
            Discards = [white],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var callable = state with { Seats = seats };
        Assert.True(ExternalMjaiProcess.TryGetUniqueCallableRiverOfferKey(callable, out string key));
        Assert.Equal("1|P", key);
    }

    [Fact]
    public void Position_fingerprint_ignores_transient_legal_surface_changes()
    {
        Tile tile = Tile.FromId(4);
        var discard = StateSnapshot.Empty with
        {
            Hand = [tile],
            WallRemaining = 40,
            Legal = new LegalActions(ActionFlags.Discard, [tile], [], [], []),
        };
        var riichiSurface = discard with
        {
            Legal = new LegalActions(ActionFlags.Discard | ActionFlags.Riichi, [tile], [], [], []),
        };

        Assert.Equal(
            ExternalMjaiProcess.PositionFingerprint(discard),
            ExternalMjaiProcess.PositionFingerprint(riichiSurface));
    }

    [Fact]
    public void Pass_is_not_still_legal_on_a_discard_only_surface()
    {
        Tile tile = Tile.FromId(4);
        var discard = StateSnapshot.Empty with
        {
            Hand = [tile],
            Legal = new LegalActions(ActionFlags.Discard, [tile], [], [], []),
        };
        var pass = discard with
        {
            Legal = new LegalActions(ActionFlags.Pass, [], [], [], []),
        };

        Assert.False(AutoPlayLoop.IsChoiceStillLegal(discard, ActionChoice.Pass("sentinel")));
        Assert.True(AutoPlayLoop.IsChoiceStillLegal(pass, ActionChoice.Pass("Akochan: pass")));
    }

    [Theory]
    [InlineData("chi", false)]
    [InlineData("pon", false)]
    [InlineData("daiminkan", false)]
    [InlineData("ankan", false)]
    [InlineData("kakan", false)]
    public void Own_open_call_is_synchronization_only_after_atomic_call_response(string type, bool expected)
    {
        string json = $"[{{\"type\":\"{type}\",\"actor\":0,\"pai\":\"3m\"}}]";

        Assert.Equal(expected, ExternalMjaiProcess.BatchExpectsDecision(json, ourSeat: 0));
    }

    [Fact]
    public void Actionable_draw_is_repaired_when_the_same_hand_was_seen_before_discard_became_legal()
    {
        Tile drawn = Tile.FromId(13);
        var hand = Enumerable.Range(0, 13)
            .Select(i => Tile.FromId(i % Tile.Count34))
            .Append(drawn)
            .ToArray();
        var transitional = StateSnapshot.Empty with
        {
            Hand = hand,
            WallRemaining = 60,
            DoraIndicators = [Tile.FromId(4)],
            Legal = LegalActions.None,
            AddonStateCode = 22,
        };
        var actionable = transitional with
        {
            Legal = new LegalActions(ActionFlags.Discard, hand, [], [], []),
            AddonStateCode = 6,
        };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(transitional);
        MjaiEventBatch batch = tracker.BuildBatch(actionable);
        JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();

        Assert.Single(events);
        Assert.Equal("tsumo", events[0]!["type"]!.GetValue<string>());
        Assert.Equal(0, events[0]!["actor"]!.GetValue<int>());
        Assert.Equal(MjaiJson.EncodeTile(drawn), events[0]!["pai"]!.GetValue<string>());
        Assert.Contains("合法手表示の遅延", batch.Status);
        Assert.True(ExternalMjaiProcess.BatchExpectsDecision(batch.Json, ourSeat: 0));
    }

    [Fact]
    public void Actionable_draw_withholds_opponent_backlog_until_own_discard_is_answered()
    {
        Tile[] hand14 = Enumerable.Range(0, 14)
            .Select(i => Tile.FromId(i % Tile.Count34))
            .ToArray();
        Tile discarded = hand14[0];
        Tile[] hand13 = hand14.Skip(1).ToArray();
        Tile drawn = Tile.FromId(22);
        Tile[] nextHand14 = hand13.Append(drawn).ToArray();
        Tile dora = Tile.FromId(12);

        var firstDraw = StateSnapshot.Empty with
        {
            Hand = hand14,
            WallRemaining = 60,
            DoraIndicators = [dora],
            Legal = new LegalActions(ActionFlags.Discard, hand14, [], [], []),
            AddonStateCode = 6,
        };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(firstDraw);
        tracker.NoteChoice(ActionChoice.Discard(discarded), firstDraw);

        SeatView[] afterDiscardSeats = firstDraw.Seats.ToArray();
        afterDiscardSeats[0] = afterDiscardSeats[0] with
        {
            Discards = [discarded],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var afterDiscard = firstDraw with
        {
            Hand = hand13,
            Seats = afterDiscardSeats,
            WallRemaining = 59,
            Legal = LegalActions.None,
            AddonStateCode = 15,
        };
        _ = tracker.BuildBatch(afterDiscard);

        // EMJ publishes the new 14-tile hand before the Discard flag and before
        // the opponent river images are all available.
        var transitional = afterDiscard with
        {
            Hand = nextHand14,
            WallRemaining = 56,
            Legal = LegalActions.None,
            AddonStateCode = 9,
        };
        _ = tracker.BuildBatch(transitional);

        SeatView[] actionableSeats = transitional.Seats.ToArray();
        for (int seat = 1; seat < 4; seat++)
        {
            Tile opponentDiscard = Tile.FromId(27 + seat);
            actionableSeats[seat] = actionableSeats[seat] with
            {
                Discards = [opponentDiscard],
                DiscardIsTedashi = [true],
                DiscardCount = 1,
            };
        }
        var actionable = transitional with
        {
            Seats = actionableSeats,
            Legal = new LegalActions(ActionFlags.Discard, nextHand14, [], [], []),
            AddonStateCode = 6,
        };

        MjaiEventBatch batch = tracker.BuildBatch(actionable);
        JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();
        JsonObject last = events[^1]!.AsObject();

        // Opponent turns stay withheld so Mortal answers our discard first.
        // Consuming them here used to skip Chi/Pass decisions (events=0 later).
        Assert.Single(events);
        Assert.Equal("tsumo", last["type"]!.GetValue<string>());
        Assert.Equal(0, last["actor"]!.GetValue<int>());
        Assert.Equal(MjaiJson.EncodeTile(drawn), last["pai"]!.GetValue<string>());
        Assert.True(ExternalMjaiProcess.BatchExpectsDecision(batch.Json, ourSeat: 0));
        Assert.Contains("合法手表示の遅延", batch.Status);

        Tile ourNextDiscard = drawn;
        tracker.NoteChoice(ActionChoice.Discard(ourNextDiscard), actionable);
        SeatView[] postSeats = actionable.Seats.ToArray();
        postSeats[0] = postSeats[0] with
        {
            Discards = postSeats[0].Discards.Append(ourNextDiscard).ToArray(),
            DiscardIsTedashi = postSeats[0].DiscardIsTedashi.Append(true).ToArray(),
            DiscardCount = postSeats[0].DiscardCount + 1,
        };
        var afterOurDiscard = actionable with
        {
            Hand = hand13,
            Seats = postSeats,
            WallRemaining = 55,
            Legal = LegalActions.None,
            AddonStateCode = 15,
        };
        MjaiEventBatch followUp = tracker.BuildBatch(afterOurDiscard);
        JsonArray followEvents = JsonNode.Parse(followUp.Json)!.AsArray();
        Assert.True(followEvents.Count >= 4, $"expected own dahai + withheld opponent turns, got {followUp.Json}");
        Assert.Contains(followEvents, node =>
            node is JsonObject obj
            && string.Equals(obj["type"]?.GetValue<string>(), "dahai", StringComparison.Ordinal)
            && obj["actor"]?.GetValue<int>() == 0);
        Assert.Contains(followEvents, node =>
            node is JsonObject obj
            && string.Equals(obj["type"]?.GetValue<string>(), "dahai", StringComparison.Ordinal)
            && obj["actor"]?.GetValue<int>() is > 0);
    }

    [Fact]
    public void Pending_retained_choice_does_not_republish_stale_discard_on_call_prompt()
    {
        Tile[] hand14 = Enumerable.Range(0, 14).Select(i => Tile.FromId(i % Tile.Count34)).ToArray();
        Tile discardTile = hand14[0];
        Tile claim = Tile.FromId(26);
        var chi = new MeldCandidate(MeldKind.Chi, claim, [Tile.FromId(24), Tile.FromId(25)], FromSeat: 3);
        Tile[] hand13 = hand14.Skip(1).ToArray();
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = [claim],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var callPrompt = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand13,
            Seats = seats,
            Legal = new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [chi], []),
            AddonStateCode = 15,
        };

        using var process = new ExternalMjaiProcess(new StubPluginLog(), string.Empty);
        SetPrivateField(process, "lastChoice", ActionChoice.Discard(discardTile, "Mortal: discard"));
        SetPrivateField(process, "lastFingerprint", "stale-discard");
        SetPrivateField(process, "lastPositionFingerprint", "stale-position");
        SetPrivateField(process, "pendingFingerprint", ExternalMjaiProcess.Fingerprint(callPrompt));
        SetPrivateField(process, "backgroundTask", Task.Delay(Timeout.Infinite));

        Assert.False(process.TryGetPendingRetainedChoice(callPrompt, out _));
    }

    [Fact]
    public void Mortal_live_pass_call_response_is_retained_and_published()
    {
        Tile nineSou = Tile.FromId(26);
        Tile sevenSou = Tile.FromId(24);
        Tile eightSou = Tile.FromId(25);
        var chi = new MeldCandidate(MeldKind.Chi, nineSou, [sevenSou, eightSou], FromSeat: 3);
        Tile[] hand13 = Enumerable.Range(0, 13).Select(i => Tile.FromId(i % Tile.Count34)).ToArray();
        hand13[0] = sevenSou;
        hand13[1] = eightSou;
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = [nineSou],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var liveCall = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand13,
            Seats = seats,
            DoraIndicators = [Tile.FromId(4)],
            Legal = new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [chi], []),
            AddonStateCode = 15,
        };
        JsonObject response = JsonNode.Parse("""{"type":"none"}""")!.AsObject();

        using var process = new ExternalMjaiProcess(new StubPluginLog(), string.Empty);
        SetPrivateField(process, "deferredCallOfferKey", "3|9s");
        SetPrivateField(process, "deferredCallResponse", response);
        SetPrivateField(process, "latestOpponentDiscardKey", "3|9s");

        Assert.True(process.TryGetDeferredCallChoice(liveCall, out ActionChoice choice));
        Assert.Equal(ActionKind.Pass, choice.Kind);
    }

    [Fact]
    public void Deferred_call_response_is_never_attached_to_a_self_discard_surface()
    {
        Tile[] hand14 = Enumerable.Range(0, 14)
            .Select(i => Tile.FromId(i % Tile.Count34))
            .ToArray();
        var selfDecision = StateSnapshot.Empty with
        {
            Hand = hand14,
            Legal = new LegalActions(ActionFlags.Discard, hand14, [], [], []),
            AddonStateCode = 30,
        };
        var pendingCallSurface = selfDecision with
        {
            Hand = hand14[..13],
            Legal = LegalActions.None,
            AddonStateCode = 15,
        };
        var liveCallSurface = pendingCallSurface with
        {
            Legal = new LegalActions(ActionFlags.Pass, [], [], [], []),
        };

        Assert.False(ExternalMjaiProcess.CanDeferCallResponse(selfDecision));
        Assert.True(ExternalMjaiProcess.CanDeferCallResponse(pendingCallSurface));
        Assert.False(ExternalMjaiProcess.CanDeferCallResponse(liveCallSurface));
    }

    [Fact]
    public void Confirmed_pon_is_sent_once_as_the_post_call_discard_trigger_even_when_meld_snapshot_lags()
    {
        Tile claim = Tile.FromId(4); // 5m
        Tile[] initialHand =
        [
            claim, claim,
            Tile.FromId(0), Tile.FromId(1), Tile.FromId(2), Tile.FromId(9), Tile.FromId(10),
            Tile.FromId(11), Tile.FromId(18), Tile.FromId(19), Tile.FromId(20), Tile.FromId(27), Tile.FromId(28),
        ];
        var initial = StateSnapshot.Empty with
        {
            Hand = initialHand,
            WallRemaining = 60,
            DoraIndicators = [Tile.FromId(12)],
            Legal = LegalActions.None,
        };
        var candidate = new MeldCandidate(MeldKind.Pon, claim, [claim, claim], FromSeat: 3);
        SeatView[] seats = initial.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = [claim],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var prompt = initial with
        {
            Seats = seats,
            Legal = new LegalActions(ActionFlags.Pon | ActionFlags.Pass, [], [candidate], [], []),
            AddonStateCode = 15,
        };
        var postCall = prompt with
        {
            Hand = initialHand.Skip(2).ToArray(),
            OurMelds = [], // Reproduces the EMJ meld-tracker lag seen in the field.
            Legal = new LegalActions(ActionFlags.Discard, initialHand.Skip(2).ToArray(), [], [], []),
            AddonStateCode = 6,
        };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(initial);
        _ = tracker.BuildBatch(prompt);
        tracker.NoteChoice(new ActionChoice(ActionKind.Pon, Call: candidate, Reasoning: "Akochan: pon"), prompt);

        MjaiEventBatch batch = tracker.BuildBatch(postCall);
        JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();

        Assert.Single(events);
        Assert.Equal("pon", events[0]!["type"]!.GetValue<string>());
        Assert.Equal(0, events[0]!["actor"]!.GetValue<int>());
        Assert.Equal(3, events[0]!["target"]!.GetValue<int>());
        Assert.Equal("5m", events[0]!["pai"]!.GetValue<string>());
        Assert.Equal(2, events[0]!["consumed"]!.AsArray().Count);
        Assert.False(ExternalMjaiProcess.BatchExpectsDecision(batch.Json, ourSeat: 0));

        MjaiEventBatch second = tracker.BuildBatch(postCall);
        JsonArray secondEvents = JsonNode.Parse(second.Json)!.AsArray();
        Assert.DoesNotContain(secondEvents, evt =>
            string.Equals(evt?["type"]?.GetValue<string>(), "pon", StringComparison.Ordinal));
    }

    [Fact]
    public void Deferred_decision_chi_is_promoted_to_the_shared_choice_cache()
    {
        Tile claim = Tile.FromId(11); // 3p
        Tile two = Tile.FromId(10);
        Tile four = Tile.FromId(12);
        Tile discard = Tile.FromId(25); // 8s
        var candidate = new MeldCandidate(MeldKind.Chi, claim, [two, four], FromSeat: 3);
        var state = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = [two, claim, claim, four, discard],
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Chi | ActionFlags.Pass,
                [],
                [new MeldCandidate(MeldKind.Pon, claim, [claim, claim], FromSeat: 3)],
                [candidate],
                []),
            AddonStateCode = 15,
        };
        JsonObject response = JsonNode.Parse(
            """{"type":"chi","actor":0,"target":3,"pai":"3p","consumed":["2p","4p"],"_post_call_pai":"8s"}""")!.AsObject();

        using var process = new ExternalMjaiProcess(
            new StubPluginLog(),
            string.Empty,
            ExternalEngineKind.AkochanComparison);
        SetPrivateField(
            process,
            "deferredDecisionPositionFingerprint",
            ExternalMjaiProcess.PositionFingerprint(state));
        SetPrivateField(process, "deferredDecisionResponse", response);

        Assert.True(process.TryGetDeferredDecisionChoice(state, out ActionChoice first));
        Assert.Equal(ActionKind.Chi, first.Kind);
        Assert.Equal(discard, first.PostCallDiscardTile!.Value);

        // StateAggregator, UI and AutoPlayLoop can all ask the same policy in one
        // frame. Every caller must see the same consumed deferred answer instead
        // of starting a second query that can overwrite Chi with Pon.
        Assert.True(process.TryGetCachedChoice(state, out ActionChoice cached));
        Assert.Equal(first, cached);
    }

    [Fact]
    public void Mortal_deferred_call_none_is_published_when_live_chi_pass_appears()
    {
        // Field failure on v0.8.1.12: Mortal answered before Chi/Pass UI, the
        // offer stayed synchronized, later polls were events=0, and the overlay
        // stuck on Calculating…. Retained none must become Pass on the live prompt.
        Tile nineSou = Tile.FromId(26); // 9s
        Tile sevenSou = Tile.FromId(24);
        Tile eightSou = Tile.FromId(25);
        var chi = new MeldCandidate(MeldKind.Chi, nineSou, [sevenSou, eightSou], FromSeat: 3);
        Tile[] hand10 =
        [
            sevenSou, eightSou,
            Tile.FromId(0), Tile.FromId(1), Tile.FromId(2), Tile.FromId(9), Tile.FromId(10),
            Tile.FromId(11), Tile.FromId(18), Tile.FromId(27),
        ];
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = [nineSou],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var liveCall = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand10,
            Seats = seats,
            DoraIndicators = [Tile.FromId(4)],
            Legal = new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [chi], []),
            AddonStateCode = 15,
        };
        JsonObject response = JsonNode.Parse("""{"type":"none"}""")!.AsObject();

        using var process = new ExternalMjaiProcess(new StubPluginLog(), string.Empty);
        SetPrivateField(process, "deferredCallOfferKey", "3|9s");
        SetPrivateField(process, "deferredCallResponse", response);
        SetPrivateField(process, "latestOpponentDiscardKey", "3|9s");

        Assert.True(process.TryGetDeferredCallChoice(liveCall, out ActionChoice choice));
        Assert.Equal(ActionKind.Pass, choice.Kind);
        Assert.Contains("Mortal", choice.Reasoning, StringComparison.Ordinal);
        Assert.True(process.TryGetCachedChoice(liveCall, out ActionChoice cached));
        Assert.Equal(choice, cached);
    }

    [Fact]
    public void Mortal_call_response_is_deferred_on_open_hand_before_pass_flag()
    {
        // Open-hand call shape (10 tiles) must retain chi/none until Pass appears.
        // The old guard only deferred closed 13-tile hands and dropped 298k answers.
        var pendingOpenHand = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = Enumerable.Range(0, 10).Select(i => Tile.FromId(i % Tile.Count34)).ToArray(),
            Legal = LegalActions.None,
            AddonStateCode = 15,
        };
        JsonObject none = JsonNode.Parse("""{"type":"none"}""")!.AsObject();
        JsonObject chi = JsonNode.Parse(
            """{"type":"chi","actor":0,"target":3,"pai":"9s","consumed":["7s","8s"]}""")!.AsObject();

        Assert.True(ExternalMjaiProcess.ShouldDeferTransientLegalSurface(
            ExternalEngineKind.Primary, true, none, pendingOpenHand));
        Assert.True(ExternalMjaiProcess.ShouldDeferTransientLegalSurface(
            ExternalEngineKind.Primary, true, chi, pendingOpenHand));
        Assert.True(ExternalMjaiProcess.CanDeferCallResponse(pendingOpenHand));
    }

    [Fact]
    public void Confirmed_pon_with_atomic_discard_never_fabricates_a_post_call_tsumo()
    {
        Tile claim = Tile.FromId(4); // 5m
        Tile exactDiscard = Tile.FromId(0); // 1m
        Tile[] beforeCall =
        [
            claim, claim, exactDiscard,
            Tile.FromId(1), Tile.FromId(2), Tile.FromId(9), Tile.FromId(10),
            Tile.FromId(11), Tile.FromId(18), Tile.FromId(19), Tile.FromId(20), Tile.FromId(27), Tile.FromId(28),
        ];
        var candidate = new MeldCandidate(MeldKind.Pon, claim, [claim, claim], FromSeat: 3);
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = [claim],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var prompt = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = beforeCall,
            Seats = seats,
            WallRemaining = 60,
            DoraIndicators = [Tile.FromId(12)],
            Legal = new LegalActions(ActionFlags.Pon | ActionFlags.Pass, [], [candidate], [], []),
            AddonStateCode = 15,
        };
        Tile[] postCallHand = beforeCall.Skip(2).ToArray();
        var postCall = prompt with
        {
            Hand = postCallHand,
            Legal = new LegalActions(ActionFlags.Discard, postCallHand, [], [], []),
            AddonStateCode = 6,
        };
        var choice = new ActionChoice(ActionKind.Pon, Call: candidate, Reasoning: "Akochan: pon")
        {
            PostCallDiscardTile = exactDiscard,
        };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(prompt);
        tracker.NoteChoice(choice, prompt);

        MjaiEventBatch callBatch = tracker.BuildBatch(postCall);
        JsonArray callEvents = JsonNode.Parse(callBatch.Json)!.AsArray();
        Assert.Single(callEvents);
        Assert.Equal("pon", callEvents[0]!["type"]!.GetValue<string>());

        MjaiEventBatch waitingBatch = tracker.BuildBatch(postCall);
        JsonArray waitingEvents = JsonNode.Parse(waitingBatch.Json)!.AsArray();
        Assert.Empty(waitingEvents);
        Assert.DoesNotContain(waitingEvents, evt =>
            string.Equals(evt?["type"]?.GetValue<string>(), "tsumo", StringComparison.Ordinal));
        Assert.Contains("確定打牌", waitingBatch.Status);

        SeatView[] afterSeats = seats.ToArray();
        afterSeats[0] = afterSeats[0] with
        {
            Discards = [exactDiscard],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var afterDiscard = postCall with
        {
            Hand = postCallHand.Where((tile, index) => index != 0).ToArray(),
            Seats = afterSeats,
            Legal = LegalActions.None,
            AddonStateCode = 15,
        };
        MjaiEventBatch discardBatch = tracker.BuildBatch(afterDiscard);
        JsonArray discardEvents = JsonNode.Parse(discardBatch.Json)!.AsArray();
        JsonObject dahai = discardEvents
            .Select(evt => evt!.AsObject())
            .Single(evt => evt["type"]!.GetValue<string>() == "dahai" && evt["actor"]!.GetValue<int>() == 0);
        Assert.Equal("1m", dahai["pai"]!.GetValue<string>());
    }

    [Fact]
    public void Exact_post_call_discard_blocks_a_second_akochan_query_as_soon_as_the_post_call_surface_appears()
    {
        Tile claim = Tile.FromId(11);
        Tile discard = Tile.FromId(25);
        var candidate = new MeldCandidate(MeldKind.Chi, claim, [Tile.FromId(10), Tile.FromId(12)], 3);
        var choice = new ActionChoice(ActionKind.Chi, Call: candidate)
        {
            PostCallDiscardTile = discard,
        };
        Tile[] hand11 = Enumerable.Range(0, 11).Select(i => Tile.FromId(i)).ToArray();
        var postCall = StateSnapshot.Empty with
        {
            Hand = hand11,
            Legal = new LegalActions(ActionFlags.Discard, hand11, [], [], []),
            AddonStateCode = 30,
        };

        // The 11-tile discard surface is itself the structural proof needed
        // to block a second query; it can appear a few milliseconds before the
        // AutoPlayLoop publishes NotifyCommittedAction.
        Assert.True(ExternalMjaiProcess.IsExactPostCallDiscardPending(postCall, choice));

        var staleCallSurface = postCall with
        {
            Legal = new LegalActions(ActionFlags.Pon | ActionFlags.Pass, [], [candidate], [], []),
            AddonStateCode = 15,
        };
        Assert.True(ExternalMjaiProcess.IsAcceptedOpenCallTransition(staleCallSurface, choice));
        Assert.False(ExternalMjaiProcess.IsExactPostCallDiscardPending(staleCallSurface, choice));

        Assert.False(ExternalMjaiProcess.IsAcceptedOpenCallTransition(
            postCall,
            choice with { PostCallDiscardTile = null }));
        Assert.False(ExternalMjaiProcess.IsExactPostCallDiscardPending(
            postCall,
            choice with { PostCallDiscardTile = null }));
        Assert.False(ExternalMjaiProcess.IsExactPostCallDiscardPending(
            postCall with { Hand = Enumerable.Range(0, 13).Select(i => Tile.FromId(i)).ToArray() },
            choice));
    }

    [Fact]
    public void Confirmed_call_is_replayed_only_after_a_fresh_native_process_start()
    {
        Tile claim = Tile.FromId(4);
        var candidate = new MeldCandidate(MeldKind.Pon, claim, [claim, claim], FromSeat: 3);
        var choice = new ActionChoice(ActionKind.Pon, Call: candidate, Reasoning: "Akochan: pon");

        Assert.False(ExternalMjaiProcess.ShouldReplayCommittedCallAfterProcessStart(
            processJustStarted: false,
            committedCall: choice,
            committedCallCanReplay: true));
        Assert.True(ExternalMjaiProcess.ShouldReplayCommittedCallAfterProcessStart(
            processJustStarted: true,
            committedCall: choice,
            committedCallCanReplay: true));
        Assert.False(ExternalMjaiProcess.ShouldReplayCommittedCallAfterProcessStart(
            processJustStarted: true,
            committedCall: choice,
            committedCallCanReplay: false));
        Assert.False(ExternalMjaiProcess.ShouldReplayCommittedCallAfterProcessStart(
            processJustStarted: true,
            committedCall: null,
            committedCallCanReplay: true));
    }

    [Fact]
    public void Confirmed_pon_survives_tracker_reset_and_rebuilds_the_mandatory_discard_request()
    {
        Tile claim = Tile.FromId(4); // 5m
        Tile[] beforeCall =
        [
            claim, claim,
            Tile.FromId(0), Tile.FromId(1), Tile.FromId(2), Tile.FromId(9), Tile.FromId(10),
            Tile.FromId(11), Tile.FromId(18), Tile.FromId(19), Tile.FromId(20), Tile.FromId(27), Tile.FromId(28),
        ];
        var candidate = new MeldCandidate(MeldKind.Pon, claim, [claim, claim], FromSeat: 3);
        Tile[] afterCall = beforeCall.Skip(2).ToArray();
        var state = StateSnapshot.Empty with
        {
            Hand = afterCall,
            WallRemaining = 60,
            DoraIndicators = [Tile.FromId(12)],
            Legal = new LegalActions(ActionFlags.Discard, afterCall, [], [], []),
            AddonStateCode = 15,
        };

        var tracker = new MjaiSessionTracker();
        tracker.NoteChoice(new ActionChoice(ActionKind.Pon, Call: candidate, Reasoning: "Akochan: pon"), state);

        // Starting/restarting the native process resets the ordered session, but
        // must not discard the exact call that FFXIV has already committed.
        tracker.Reset(preserveCommittedOwnCall: true);
        MjaiEventBatch batch = tracker.BuildBatch(state);
        JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();

        Assert.True(batch.StartsGame);
        Assert.Equal(5, events.Count);
        Assert.Equal("start_game", events[0]!["type"]!.GetValue<string>());
        Assert.Equal(13, events[1]!["tehais"]!.AsArray()[0]!.AsArray().Count);
        Assert.Equal("dahai", events[3]!["type"]!.GetValue<string>());
        Assert.Equal("5m", events[3]!["pai"]!.GetValue<string>());
        Assert.Equal("pon", events[4]!["type"]!.GetValue<string>());
        Assert.Equal(0, events[4]!["actor"]!.GetValue<int>());
        Assert.Equal(3, events[4]!["target"]!.GetValue<int>());
        Assert.False(ExternalMjaiProcess.BatchExpectsDecision(batch.Json, ourSeat: 0));
        Assert.Contains("mandatory discard", batch.Status);
    }

    [Fact]
    public void Committed_call_recovery_is_released_only_after_the_game_advances_our_discard()
    {
        Tile[] hand11 = Enumerable.Range(0, 11)
            .Select(i => Tile.FromId(i % Tile.Count34))
            .ToArray();
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[0] = seats[0] with
        {
            Discards = [Tile.FromId(27), Tile.FromId(28), Tile.FromId(29)],
            DiscardIsTedashi = [true, true, true],
            DiscardCount = 3,
        };
        var postCall = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand11,
            Seats = seats,
            WallRemaining = 52,
            Legal = new LegalActions(ActionFlags.Discard, hand11, [], [], []),
            AddonStateCode = 15,
        };

        Assert.False(ExternalMjaiProcess.IsCommittedCallFollowUpObserved(
            postCall,
            confirmedByGame: false,
            postCallHandCount: -1,
            discardCountAtDispatch: 3,
            wallAtDispatch: 52));
        Assert.False(ExternalMjaiProcess.IsCommittedCallFollowUpObserved(
            postCall,
            confirmedByGame: true,
            postCallHandCount: 11,
            discardCountAtDispatch: 3,
            wallAtDispatch: 52));

        var afterHandShrink = postCall with { Hand = hand11[..10] };
        Assert.True(ExternalMjaiProcess.IsCommittedCallFollowUpObserved(
            afterHandShrink,
            confirmedByGame: true,
            postCallHandCount: 11,
            discardCountAtDispatch: 3,
            wallAtDispatch: 52));

        SeatView[] advancedSeats = seats.ToArray();
        advancedSeats[0] = advancedSeats[0] with
        {
            Discards = [Tile.FromId(27), Tile.FromId(28), Tile.FromId(29), Tile.FromId(30)],
            DiscardIsTedashi = [true, true, true, true],
            DiscardCount = 4,
        };
        var afterRiverAdvance = postCall with { Seats = advancedSeats };
        Assert.True(ExternalMjaiProcess.IsCommittedCallFollowUpObserved(
            afterRiverAdvance,
            confirmedByGame: false,
            postCallHandCount: -1,
            discardCountAtDispatch: 3,
            wallAtDispatch: 52));
    }

    [Fact]
    public void Snapshot_observed_call_is_not_republished_when_commit_notification_arrives_after_batch_build()
    {
        Tile claim = Tile.FromId(30); // 北
        Tile exactDiscard = Tile.FromId(8); // 9m
        Tile[] beforeCall =
        [
            claim, claim, exactDiscard,
            Tile.FromId(5), Tile.FromId(11), Tile.FromId(12), Tile.FromId(17),
            Tile.FromId(24), Tile.FromId(25), Tile.FromId(28), Tile.FromId(28),
            Tile.FromId(32), Tile.FromId(33),
        ];
        var candidate = new MeldCandidate(MeldKind.Pon, claim, [claim, claim], FromSeat: 1);
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[1] = seats[1] with
        {
            Discards = [claim],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var prompt = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = beforeCall,
            Seats = seats,
            WallRemaining = 68,
            DoraIndicators = [Tile.FromId(33)],
            Legal = new LegalActions(ActionFlags.Pon | ActionFlags.Pass, [], [candidate], [], []),
            AddonStateCode = 15,
        };
        Tile[] hand11 = beforeCall.Where((_, index) => index >= 2).ToArray();
        var committedMeld = Meld.FromAcceptedCandidate(candidate);
        var committed = prompt with
        {
            Hand = hand11,
            OurMelds = [committedMeld],
            Legal = new LegalActions(ActionFlags.Discard, hand11, [], [], []),
            AddonStateCode = 6,
        };
        var choice = new ActionChoice(ActionKind.Pon, Call: candidate, Reasoning: "Akochan: pon")
        {
            PostCallDiscardTile = exactDiscard,
        };

        var tracker = new MjaiSessionTracker();
        MjaiEventBatch bootstrap = tracker.BuildBatch(prompt);
        tracker.NoteBatchSent(bootstrap.Json);

        // Reproduce the real ordering: the snapshot batch notices the meld first,
        // then AutoPlayLoop publishes the confirmed ActionChoice a few ms later.
        MjaiEventBatch inferredCallBatch = tracker.BuildBatch(committed);
        JsonArray inferredEvents = JsonNode.Parse(inferredCallBatch.Json)!.AsArray();
        Assert.Single(inferredEvents, evt => evt?["type"]?.GetValue<string>() == "pon");

        tracker.NoteChoice(choice, committed);

        SeatView[] afterSeats = seats.ToArray();
        afterSeats[0] = afterSeats[0] with
        {
            Discards = [exactDiscard],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var afterDiscard = committed with
        {
            Hand = hand11.Where((tile, index) => index != 0).ToArray(),
            Seats = afterSeats,
            Legal = LegalActions.None,
            AddonStateCode = 15,
        };
        MjaiEventBatch followUpBatch = tracker.BuildBatch(afterDiscard);
        JsonArray followUpEvents = JsonNode.Parse(followUpBatch.Json)!.AsArray();

        Assert.DoesNotContain(followUpEvents, evt => evt?["type"]?.GetValue<string>() == "pon");
        JsonObject dahai = followUpEvents
            .Select(evt => evt!.AsObject())
            .Single(evt => evt["type"]!.GetValue<string>() == "dahai" && evt["actor"]!.GetValue<int>() == 0);
        Assert.Equal("9m", dahai["pai"]!.GetValue<string>());
    }

    [Fact]
    public void Bundled_akochan_host_source_allows_post_call_decisions()
    {
        string sourcePath = FindRepositoryFile("third-party", "AkochanHostSource", "akochan_pipe.cpp");
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("actor == player_id", source);
        Assert.Contains("can_act=true", source);
    }

    [Fact]
    public void Bundled_akochan_runtime_contains_post_call_trigger_patch()
    {
        string runtimePath = FindRepositoryFile("third-party", "AkochanRuntime", "akochan_pipe.exe");
        byte[] runtime = File.ReadAllBytes(runtimePath);
        const int patchOffset = 0xCA40F;
        byte[] expected = [0xE9, 0x4F, 0xF9, 0xFF, 0xFF, 0x90];

        Assert.True(runtime.Length >= patchOffset + expected.Length);
        Assert.Equal(expected, runtime[patchOffset..(patchOffset + expected.Length)]);
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    [Fact]
    public void Opponent_pon_without_claimed_from_infers_target_from_river()
    {
        Tile claim = Tile.FromId(4); // 5m
        Tile[] hand13 = Enumerable.Range(0, 13).Select(Tile.FromId).ToArray();
        // Start from a fresh closed hand (no prior river) so the tracker keeps an
        // ordered session instead of taking the mid-hand stateless resync path.
        var opening = StateSnapshot.Empty with
        {
            Hand = hand13,
            WallRemaining = 70,
            DoraIndicators = [Tile.FromId(12)],
            Legal = LegalActions.None,
            AddonStateCode = 6,
        };
        SeatView[] beforeSeats = opening.Seats.ToArray();
        beforeSeats[3] = beforeSeats[3] with
        {
            Discards = [claim],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var before = opening with
        {
            WallRemaining = 60,
            Seats = beforeSeats,
        };

        SeatView[] afterSeats = beforeSeats.ToArray();
        afterSeats[3] = afterSeats[3] with
        {
            Discards = [],
            DiscardIsTedashi = [],
            DiscardCount = 0,
        };
        afterSeats[1] = afterSeats[1] with
        {
            Melds = [Meld.Pon(claim, claim, fromSeat: -1)],
        };
        var after = before with { Seats = afterSeats };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(opening);
        MjaiEventBatch discardBatch = tracker.BuildBatch(before);
        tracker.NoteBatchSent(discardBatch.Json);
        MjaiEventBatch batch = tracker.BuildBatch(after);
        JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();

        JsonNode? pon = events.FirstOrDefault(evt =>
            string.Equals(evt?["type"]?.GetValue<string>(), "pon", StringComparison.Ordinal));
        Assert.NotNull(pon);
        Assert.Equal(1, pon!["actor"]!.GetValue<int>());
        Assert.Equal(3, pon["target"]!.GetValue<int>());
        Assert.Equal("5m", pon["pai"]!.GetValue<string>());
    }

    [Fact]
    public void Opponent_chi_without_claimed_from_uses_kamicha_as_target()
    {
        Tile low = Tile.FromId(0); // 1m
        // Distinct pin/sou tiles so the chi'd 1m2m3m never trips the
        // four-copies budget against our own hand.
        Tile[] hand13 = Enumerable.Range(9, 13).Select(Tile.FromId).ToArray();
        var opening = StateSnapshot.Empty with
        {
            Hand = hand13,
            WallRemaining = 60,
            DoraIndicators = [Tile.FromId(12)],
            Legal = LegalActions.None,
            AddonStateCode = 6,
        };
        // The claimed 1m must have actually been discarded by kamicha (seat 1)
        // and delivered to the engine; a chi claiming a never-discarded tile is
        // withheld since the 2026-08-01 shaken-meld poisoning fix.
        SeatView[] beforeSeats = opening.Seats.ToArray();
        beforeSeats[1] = beforeSeats[1] with
        {
            Discards = [low],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        var before = opening with { Seats = beforeSeats };
        SeatView[] afterSeats = beforeSeats.ToArray();
        afterSeats[1] = afterSeats[1] with
        {
            Discards = [],
            DiscardIsTedashi = [],
            DiscardCount = 0,
        };
        afterSeats[2] = afterSeats[2] with
        {
            Melds = [Meld.Chi(low, low, fromSeat: -1)],
        };
        var after = before with { Seats = afterSeats };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(opening);
        MjaiEventBatch discardBatch = tracker.BuildBatch(before);
        tracker.NoteBatchSent(discardBatch.Json);
        MjaiEventBatch batch = tracker.BuildBatch(after);
        JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();

        JsonNode? chi = events.FirstOrDefault(evt =>
            string.Equals(evt?["type"]?.GetValue<string>(), "chi", StringComparison.Ordinal));
        Assert.NotNull(chi);
        Assert.Equal(2, chi!["actor"]!.GetValue<int>());
        // Chi claims from the previous seat only (kamicha).
        Assert.Equal(1, chi["target"]!.GetValue<int>());
    }

    [Fact]
    public void Opponent_pon_without_target_clue_is_not_emitted()
    {
        Tile claim = Tile.FromId(27); // 1z / East
        Tile[] hand13 = Enumerable.Range(0, 13).Select(Tile.FromId).ToArray();
        var before = StateSnapshot.Empty with
        {
            Hand = hand13,
            WallRemaining = 60,
            DoraIndicators = [Tile.FromId(12)],
            Legal = LegalActions.None,
            AddonStateCode = 6,
        };
        SeatView[] afterSeats = before.Seats.ToArray();
        afterSeats[1] = afterSeats[1] with
        {
            Melds = [Meld.Pon(claim, claim, fromSeat: -1)],
        };
        var after = before with { Seats = afterSeats };

        var tracker = new MjaiSessionTracker();
        _ = tracker.BuildBatch(before);
        MjaiEventBatch batch = tracker.BuildBatch(after);
        JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();

        Assert.DoesNotContain(events, evt =>
            string.Equals(evt?["type"]?.GetValue<string>(), "pon", StringComparison.Ordinal));
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(new[] { current.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }

}
