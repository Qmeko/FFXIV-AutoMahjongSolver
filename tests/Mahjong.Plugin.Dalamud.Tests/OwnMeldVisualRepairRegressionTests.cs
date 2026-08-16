using System.Linq;
using System.Text.Json.Nodes;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Xunit;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Regression tests for the 2026-08-01 21:22:58 field incident (suggest-only
/// play): the user's committed 6m7m+8m chi was published by the EMJ visual
/// decode as 5m6m7m claiming a never-discarded 5m. The tile-witness guard then
/// withheld the chi forever, the engine never learned the call, and one frame
/// later the "draw published before the Discard flag" repair invented a
/// phantom "tsumo 8s" that poisoned Mortal's hand model (dahai 7m rejected at
/// 21:23:12, watchdog restart, instruction loss for the rest of the hand).
/// The fix rebuilds our own meld from authoritative facts: the tiles that
/// actually left the concealed hand plus the last engine-known discard per
/// opponent seat.
/// </summary>
public class OwnMeldVisualRepairRegressionTests
{
    private static readonly Tile M5 = Tile.FromId(4);
    private static readonly Tile M6 = Tile.FromId(5);
    private static readonly Tile M7 = Tile.FromId(6);
    private static readonly Tile M8 = Tile.FromId(7);
    private static readonly Tile Chun = Tile.FromId(33);

    private static readonly Tile[] PromptHand13 =
    [
        M6, M7,
        Tile.FromId(14), Tile.FromId(14), Tile.FromId(14),
        Tile.FromId(20), Tile.FromId(20),
        Tile.FromId(21), Tile.FromId(21),
        Tile.FromId(25), Tile.FromId(25),
        Tile.FromId(27), Tile.FromId(27),
    ];

    private static StateSnapshot BuildChiPrompt()
    {
        var candidate = new MeldCandidate(MeldKind.Chi, M8, [M6, M7], FromSeat: 3);
        var prompt = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = PromptHand13,
            WallRemaining = 22,
            DoraIndicators = [Tile.FromId(19)],
            Legal = new LegalActions(
                ActionFlags.Chi | ActionFlags.Pass,
                [],
                [],
                [candidate],
                []),
            AddonStateCode = 15,
        };
        SeatView[] seats = prompt.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = [M8],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        return prompt with { Seats = seats };
    }

    private static StateSnapshot BuildPostChiState(StateSnapshot prompt, Meld visualMeld)
    {
        Tile[] hand11 = prompt.Hand.Where(t => t != M6 && t != M7).ToArray();
        return prompt with
        {
            Hand = hand11,
            OurMelds = [visualMeld],
            Legal = new LegalActions(ActionFlags.Discard, hand11, [], [], []),
            AddonStateCode = 6,
        };
    }

    [Fact]
    public void Wrong_visual_chi_decode_is_repaired_from_hand_delta_and_river()
    {
        StateSnapshot prompt = BuildChiPrompt();
        var tracker = new MjaiSessionTracker();

        MjaiEventBatch offerBatch = tracker.BuildBatch(prompt);
        JsonObject offerTail = JsonNode.Parse(offerBatch.Json)!.AsArray()[^1]!.AsObject();
        Assert.Equal("dahai", offerTail["type"]!.GetValue<string>());
        Assert.Equal(3, offerTail["actor"]!.GetValue<int>());
        Assert.Equal("8m", offerTail["pai"]!.GetValue<string>());
        tracker.NoteBatchSent(offerBatch.Json);

        // The visual decode is wrong in every field that matters: 5m6m7m
        // claiming a 5m that seat 3 never discarded.
        StateSnapshot postCall = BuildPostChiState(prompt, Meld.Chi(M5, M5, fromSeat: 3));

        MjaiEventBatch callBatch = tracker.BuildBatch(postCall);
        JsonArray events = JsonNode.Parse(callBatch.Json)!.AsArray();

        JsonObject chi = Assert.Single(
            events.Select(evt => evt!.AsObject()),
            evt => evt["type"]!.GetValue<string>() == "chi");
        Assert.Equal(0, chi["actor"]!.GetValue<int>());
        Assert.Equal(3, chi["target"]!.GetValue<int>());
        Assert.Equal("8m", chi["pai"]!.GetValue<string>());
        Assert.Equal(
            new[] { "6m", "7m" },
            chi["consumed"]!.AsArray().Select(t => t!.GetValue<string>()).OrderBy(t => t).ToArray());

        // No phantom own draw next to the call.
        Assert.DoesNotContain(events, evt =>
            evt!["type"]!.GetValue<string>() == "tsumo"
            && evt["actor"]!.GetValue<int>() == 0);

        // The repaired call is the decision boundary Mortal must answer with
        // the mandatory post-call discard.
        Assert.Equal("chi", events[^1]!["type"]!.GetValue<string>());
        Assert.True(ExternalMjaiProcess.BatchEndsWithOwnCallDecision(callBatch.Json, ourSeat: 0));
    }

    [Fact]
    public void Unsynced_own_call_blocks_the_phantom_tsumo_repair_on_later_frames()
    {
        StateSnapshot prompt = BuildChiPrompt();
        var tracker = new MjaiSessionTracker();
        MjaiEventBatch offerBatch = tracker.BuildBatch(prompt);
        // The offer is intentionally NOT recorded as sent: without a witnessed
        // "3|8m" dahai the wrong visual chi cannot be repaired and must stay
        // withheld.
        Assert.True(offerBatch.EventCount > 0);

        StateSnapshot postCall = BuildPostChiState(prompt, Meld.Chi(M5, M5, fromSeat: 3));

        MjaiEventBatch first = tracker.BuildBatch(postCall);
        MjaiEventBatch second = tracker.BuildBatch(postCall);

        foreach (MjaiEventBatch batch in new[] { first, second })
        {
            if (batch.EventCount == 0)
                continue;
            JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();
            // Neither an invented own draw (the 2026-08-01 21:23:02 poison)
            // nor an unvalidated chi may reach the engine.
            Assert.DoesNotContain(events, evt =>
                evt!["type"]!.GetValue<string>() is "tsumo" or "chi"
                && evt["actor"]!.GetValue<int>() == 0);
        }
    }

    [Fact]
    public void Ambiguous_pon_claim_sources_stay_withheld()
    {
        // A pon prompt from seat 1, but seat 2 has also discarded Chun in an
        // earlier batch. The visual decode then claims the pon came from seat
        // 3 (which never discarded Chun): the witness fails and the repair
        // finds two provable sources, so the meld must stay withheld instead
        // of guessing.
        Tile[] hand13 = Enumerable.Range(0, 11).Select(Tile.FromId).Append(Chun).Append(Chun).ToArray();
        var candidate = new MeldCandidate(MeldKind.Pon, Chun, [Chun, Chun], FromSeat: 1);
        var prompt = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand13,
            WallRemaining = 40,
            DoraIndicators = [Tile.FromId(19)],
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Pass, [], [candidate], [], []),
            AddonStateCode = 15,
        };
        SeatView[] seats = prompt.Seats.ToArray();
        seats[1] = seats[1] with
        {
            Discards = [Chun],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        prompt = prompt with { Seats = seats };

        var tracker = new MjaiSessionTracker();
        MjaiEventBatch offerBatch = tracker.BuildBatch(prompt);
        tracker.NoteBatchSent(offerBatch.Json);
        tracker.NoteBatchSent(
            """[{"type":"dahai","actor":2,"pai":"C","tsumogiri":false}]""");

        Tile[] hand11 = hand13.Where(t => t != Chun).ToArray();
        StateSnapshot postCall = prompt with
        {
            Hand = hand11,
            OurMelds = [Meld.Pon(Chun, Chun, fromSeat: 3)],
            Legal = new LegalActions(ActionFlags.Discard, hand11, [], [], []),
            AddonStateCode = 6,
        };

        MjaiEventBatch batch = tracker.BuildBatch(postCall);
        if (batch.EventCount > 0)
        {
            JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();
            Assert.DoesNotContain(events, evt =>
                evt!["type"]!.GetValue<string>() is "pon" or "tsumo"
                && evt["actor"]!.GetValue<int>() == 0);
        }
    }

    [Theory]
    [InlineData(new[] { 5, 6 }, 7, "chi")]   // 6m7m + 8m
    [InlineData(new[] { 5, 6 }, 4, "chi")]   // 6m7m + 5m
    [InlineData(new[] { 5, 7 }, 6, "chi")]   // 6m8m + 7m (kanchan)
    [InlineData(new[] { 5, 6 }, 8, null)]    // 6m7m + 9m is not a run
    [InlineData(new[] { 23, 23 }, 23, "pon")]
    [InlineData(new[] { 33, 33 }, 33, "pon")]
    [InlineData(new[] { 33, 33, 33 }, 33, "daiminkan")]
    [InlineData(new[] { 27, 28 }, 29, null)] // honors never form a run
    [InlineData(new[] { 8, 9 }, 10, null)]   // 9m1p2p crosses suits
    public void Classify_own_call_derives_the_kind_from_tiles(int[] consumedIds, int claimedId, string? expected)
    {
        Tile[] consumed = consumedIds.Select(Tile.FromId).ToArray();
        Assert.Equal(expected, MjaiSessionTracker.ClassifyOwnCall(consumed, Tile.FromId(claimedId)));
    }
}
