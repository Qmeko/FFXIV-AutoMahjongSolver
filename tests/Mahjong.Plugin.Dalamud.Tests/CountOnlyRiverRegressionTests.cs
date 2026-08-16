using System.Linq;
using System.Text.Json.Nodes;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Xunit;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Regression tests for the 2026-08-01 18:04 field incident: the river reader
/// published seat discard counts one frame before the concrete tile array.
/// The tracker absorbed the counts while skipping the unknown tiles, so the
/// dahai that opened a "Pon C" window was never sent to Mortal, and the call
/// prompt repair was blocked by AlreadyHasCallOffer for minutes.
/// </summary>
public class CountOnlyRiverRegressionTests
{
    private static readonly Tile Chun = Tile.FromId(33); // C (red dragon)

    private static StateSnapshot BuildInitial() => StateSnapshot.Empty with
    {
        OurSeat = 0,
        Hand = Enumerable.Repeat(Chun, 13).ToArray(),
        DoraIndicators = [Tile.FromId(4)],
        Legal = LegalActions.None,
    };

    private static StateSnapshot WithSeat3Discard(
        StateSnapshot initial, bool decoded, bool withPrompt)
    {
        SeatView[] seats = initial.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = decoded ? [Chun] : [],
            DiscardIsTedashi = decoded ? [true] : [],
            DiscardCount = 1,
        };
        StateSnapshot state = initial with { Seats = seats };
        if (withPrompt)
        {
            state = state with
            {
                Legal = new LegalActions(
                    ActionFlags.Pon | ActionFlags.Pass,
                    [],
                    [new MeldCandidate(MeldKind.Pon, Chun, [Chun, Chun], FromSeat: 3)],
                    [],
                    []),
                AddonStateCode = 15,
            };
        }
        return state;
    }

    [Fact]
    public void Count_only_river_frame_does_not_swallow_the_discard()
    {
        var tracker = new MjaiSessionTracker();
        StateSnapshot initial = BuildInitial();
        _ = tracker.BuildBatch(initial);

        // Frame A: DiscardCount advanced, tile array still empty (decode lag).
        MjaiEventBatch countOnly = tracker.BuildBatch(
            WithSeat3Discard(initial, decoded: false, withPrompt: false));
        Assert.Equal(0, countOnly.EventCount);

        // Frame B: the tile decoded and the Pon window is live. The dahai must
        // be emitted now; before the fix the count-only frame had already
        // advanced `previous`, so this batch stayed empty and the window was
        // permanently lost.
        StateSnapshot promptState = WithSeat3Discard(initial, decoded: true, withPrompt: true);
        MjaiEventBatch decoded = tracker.BuildBatch(promptState);

        Assert.True(decoded.EventCount >= 2);
        JsonArray events = JsonNode.Parse(decoded.Json)!.AsArray();
        JsonObject last = events[^1]!.AsObject();
        Assert.Equal("dahai", last["type"]!.GetValue<string>());
        Assert.Equal(3, last["actor"]!.GetValue<int>());
        Assert.Equal("C", last["pai"]!.GetValue<string>());
        Assert.True(ExternalMjaiProcess.BatchExpectsDecision(decoded.Json, promptState.OurSeat));
    }

    [Fact]
    public void Already_has_call_offer_requires_the_dahai_to_have_been_sent()
    {
        var tracker = new MjaiSessionTracker();
        StateSnapshot initial = BuildInitial();
        _ = tracker.BuildBatch(initial);

        StateSnapshot promptState = WithSeat3Discard(initial, decoded: true, withPrompt: true);
        MjaiEventBatch batch = tracker.BuildBatch(promptState);
        Assert.True(batch.EventCount > 0);

        // The tracker snapshot has advanced past the discard, but the engine
        // has not received it: the repair path must stay available.
        Assert.False(tracker.AlreadyHasCallOffer(3, Chun));

        tracker.NoteBatchSent(batch.Json);
        Assert.True(tracker.AlreadyHasCallOffer(3, Chun));
    }

    [Fact]
    public void Offer_key_river_tip_check_rejects_stale_keys()
    {
        StateSnapshot initial = BuildInitial();
        SeatView[] seats = initial.Seats.ToArray();
        seats[1] = seats[1] with
        {
            Discards = [Tile.FromId(27), Tile.FromId(28)], // E then S
            DiscardIsTedashi = [true, true],
            DiscardCount = 2,
        };
        StateSnapshot state = initial with { Seats = seats };

        Assert.True(ExternalMjaiProcess.OfferKeyIsRiverTip(state, "1|S"));
        Assert.False(ExternalMjaiProcess.OfferKeyIsRiverTip(state, "1|E"));
        Assert.False(ExternalMjaiProcess.OfferKeyIsRiverTip(state, "2|S"));
    }
}
