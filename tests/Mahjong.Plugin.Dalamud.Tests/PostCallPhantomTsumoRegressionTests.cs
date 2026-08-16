using System.Linq;
using System.Text.Json.Nodes;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Xunit;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Regression tests for the 2026-08-01 20:50:43 field incident: our committed
/// pon reached the engine through the snapshot meld diff (AppendNewMelds, not
/// TryAppendCommittedOwnCall), and the "draw published before the Discard
/// flag" repair then invented a phantom own tsumo in the same batch. mjai has
/// no draw between a chi/pon and its mandatory discard, so the phantom tile
/// stayed in Mortal's hand model forever; ten turns later Mortal recommended
/// discarding a 9s that was no longer in the live hand and every subsequent
/// poll was rejected (permanent instruction loss at 20:51:20).
/// </summary>
public class PostCallPhantomTsumoRegressionTests
{
    private static readonly Tile Chun = Tile.FromId(33);

    private static StateSnapshot BuildPonPrompt(out MeldCandidate candidate)
    {
        Tile[] hand13 = Enumerable.Range(0, 11)
            .Select(Tile.FromId)
            .Append(Chun)
            .Append(Chun)
            .ToArray();
        candidate = new MeldCandidate(MeldKind.Pon, Chun, [Chun, Chun], FromSeat: 3);

        var prompt = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand13,
            WallRemaining = 59,
            DoraIndicators = [Tile.FromId(19)],
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Pass,
                [],
                [candidate],
                [],
                []),
            AddonStateCode = 15,
        };
        SeatView[] seats = prompt.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = [Chun],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        return prompt with { Seats = seats };
    }

    [Fact]
    public void Committed_pon_from_meld_diff_does_not_invent_a_phantom_tsumo()
    {
        StateSnapshot prompt = BuildPonPrompt(out _);

        var tracker = new MjaiSessionTracker();
        MjaiEventBatch offerBatch = tracker.BuildBatch(prompt);
        JsonArray offerEvents = JsonNode.Parse(offerBatch.Json)!.AsArray();
        JsonObject offerTail = offerEvents[^1]!.AsObject();
        Assert.Equal("dahai", offerTail["type"]!.GetValue<string>());
        Assert.Equal(3, offerTail["actor"]!.GetValue<int>());
        Assert.Equal("C", offerTail["pai"]!.GetValue<string>());
        tracker.NoteBatchSent(offerBatch.Json);

        // The pon commits without NoteChoice ever storing it (the answer came
        // from the deferred-call cache in the field), so the meld can only be
        // discovered through the snapshot meld diff.
        Tile[] hand11 = prompt.Hand.Where(t => t != Chun).ToArray();
        var postCall = prompt with
        {
            Hand = hand11,
            OurMelds = [Meld.Pon(Chun, Chun, fromSeat: 3)],
            Legal = new LegalActions(ActionFlags.Discard, hand11, [], [], []),
            AddonStateCode = 6,
        };

        MjaiEventBatch callBatch = tracker.BuildBatch(postCall);
        JsonArray events = JsonNode.Parse(callBatch.Json)!.AsArray();

        JsonObject pon = Assert.Single(
            events.Select(evt => evt!.AsObject()),
            evt => evt["type"]!.GetValue<string>() == "pon");
        Assert.Equal(0, pon["actor"]!.GetValue<int>());
        Assert.Equal(3, pon["target"]!.GetValue<int>());
        Assert.Equal("C", pon["pai"]!.GetValue<string>());

        // The phantom tsumo was the poisoning event: mjai has no draw between
        // a pon and its mandatory discard.
        Assert.DoesNotContain(events, evt =>
            evt!["type"]!.GetValue<string>() == "tsumo"
            && evt["actor"]!.GetValue<int>() == 0);

        // The call itself is the decision boundary the engine must answer.
        Assert.Equal("pon", events[^1]!["type"]!.GetValue<string>());
        Assert.True(ExternalMjaiProcess.BatchEndsWithOwnCallDecision(callBatch.Json, ourSeat: 0));
    }

    [Theory]
    [InlineData("""[{"type":"pon","actor":0,"pai":"C","target":3,"consumed":["C","C"]}]""", true)]
    [InlineData("""[{"type":"chi","actor":0,"pai":"3m","target":3,"consumed":["1m","2m"]}]""", true)]
    [InlineData("""[{"type":"daiminkan","actor":0,"pai":"C","target":3,"consumed":["C","C","C"]}]""", false)]
    [InlineData("""[{"type":"pon","actor":1,"pai":"C","target":3,"consumed":["C","C"]}]""", false)]
    [InlineData("""[{"type":"tsumo","actor":0,"pai":"5p"}]""", false)]
    [InlineData("""[{"type":"pon","actor":0,"pai":"C","target":3,"consumed":["C","C"]},{"type":"tsumo","actor":0,"pai":"9s"}]""", false)]
    [InlineData("[]", false)]
    public void Batch_ends_with_own_call_decision_detects_post_call_discard_batches(string json, bool expected)
    {
        Assert.Equal(expected, ExternalMjaiProcess.BatchEndsWithOwnCallDecision(json, ourSeat: 0));
    }
}
