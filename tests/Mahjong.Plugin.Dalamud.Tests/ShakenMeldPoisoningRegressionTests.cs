using System.Linq;
using System.Text.Json.Nodes;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Xunit;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Regression tests for the 2026-08-01 18:57 field incident: an opponent chi
/// of a discarded 7p was published by EMJ as 7p8p9p on one frame and then
/// flickered to 6s7s8s (claiming a never-discarded 6s) on the next. The
/// tracker emitted the invented chi, Mortal's tile counts broke, and the
/// engine answered every later event with "rule violation: attempt to
/// witness the fifth 8s" — a permanently lost instruction stream.
/// </summary>
public class ShakenMeldPoisoningRegressionTests
{
    private static readonly Tile Pin7 = Tile.FromId(15);
    private static readonly Tile Sou6 = Tile.FromId(23);

    private static StateSnapshot BuildInitial() => StateSnapshot.Empty with
    {
        OurSeat = 0,
        // Thirteen distinct man/pin tiles: never trips the four-copies budget.
        Hand = Enumerable.Range(0, 13).Select(Tile.FromId).ToArray(),
        DoraIndicators = [Tile.FromId(30)],
        Legal = LegalActions.None,
    };

    private static StateSnapshot WithSeat1Discard(StateSnapshot initial)
    {
        SeatView[] seats = initial.Seats.ToArray();
        seats[1] = seats[1] with
        {
            Discards = [Pin7],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        return initial with { Seats = seats };
    }

    private static StateSnapshot WithSeat2Meld(StateSnapshot withDiscard, Meld meld)
    {
        SeatView[] seats = withDiscard.Seats.ToArray();
        seats[2] = seats[2] with { Melds = [meld] };
        return withDiscard with { Seats = seats };
    }

    private static MjaiSessionTracker TrackerAfterSeat1Discard(out StateSnapshot withDiscard)
    {
        var tracker = new MjaiSessionTracker();
        StateSnapshot initial = BuildInitial();
        _ = tracker.BuildBatch(initial);

        withDiscard = WithSeat1Discard(initial);
        MjaiEventBatch discardBatch = tracker.BuildBatch(withDiscard);
        Assert.Contains("\"dahai\"", discardBatch.Json);
        tracker.NoteBatchSent(discardBatch.Json);
        return tracker;
    }

    [Fact]
    public void Chi_claiming_a_never_discarded_tile_is_withheld()
    {
        MjaiSessionTracker tracker = TrackerAfterSeat1Discard(out StateSnapshot withDiscard);

        // Shaken frame: the committed 7p chi is visually published as 6s7s8s
        // claiming a 6s that no seat ever discarded.
        Meld shaken = Meld.Chi(Sou6, claimed: Sou6, fromSeat: 1);
        MjaiEventBatch poisoned = tracker.BuildBatch(WithSeat2Meld(withDiscard, shaken));

        Assert.DoesNotContain("\"chi\"", poisoned.Json);
        Assert.NotNull(tracker.LastRejectedMeldEvent);
        Assert.Contains("6s", tracker.LastRejectedMeldEvent);
    }

    [Fact]
    public void Chi_matching_the_actual_discard_is_emitted_after_the_shaken_frame()
    {
        MjaiSessionTracker tracker = TrackerAfterSeat1Discard(out StateSnapshot withDiscard);

        Meld shaken = Meld.Chi(Sou6, claimed: Sou6, fromSeat: 1);
        StateSnapshot shakenState = WithSeat2Meld(withDiscard, shaken);
        _ = tracker.BuildBatch(shakenState);

        // Settled frame: EMJ corrects the meld back to the real 7p chi.
        Meld settled = Meld.Chi(Pin7, claimed: Pin7, fromSeat: 1);
        MjaiEventBatch corrected = tracker.BuildBatch(WithSeat2Meld(withDiscard, settled));

        JsonArray events = JsonNode.Parse(corrected.Json)!.AsArray();
        JsonObject chi = (JsonObject)Assert.Single(
            events.OfType<JsonObject>(),
            evt => evt["type"]?.GetValue<string>() == "chi");
        Assert.Equal(2, chi["actor"]!.GetValue<int>());
        Assert.Equal(1, chi["target"]!.GetValue<int>());
        Assert.Equal("7p", chi["pai"]!.GetValue<string>());
    }

    [Fact]
    public void Chi_in_the_same_batch_as_its_dahai_is_still_emitted()
    {
        var tracker = new MjaiSessionTracker();
        StateSnapshot initial = BuildInitial();
        _ = tracker.BuildBatch(initial);

        // Discard and meld arrive in one snapshot: the dahai is appended
        // earlier in the same batch, which must satisfy the witness check.
        Meld settled = Meld.Chi(Pin7, claimed: Pin7, fromSeat: 1);
        StateSnapshot state = WithSeat2Meld(WithSeat1Discard(initial), settled);
        MjaiEventBatch batch = tracker.BuildBatch(state);

        Assert.Contains("\"chi\"", batch.Json);
        Assert.Contains("\"7p\"", batch.Json);
    }

    [Theory]
    [InlineData("rule violation: attempt to witness the fifth 8s", true)]
    [InlineData("bot error: on event Tsumo { actor: 0, pai: 8s }", true)]
    [InlineData("Caused by: rule violation: attempt to witness the fifth 8s", true)]
    [InlineData("loading model weights from mortal.pth", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Engine_poisoning_is_detected_from_stderr(string? line, bool expected)
    {
        Assert.Equal(expected, ExternalMjaiProcess.IndicatesEnginePoisoning(line));
    }
}
