using System.Linq;
using System.Text.Json.Nodes;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Xunit;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Regression tests for the 2026-08-01 18:38 field incident: the player
/// discarded 8s and immediately drew another 8s, while the 13-tile hand frame
/// fell into a polling gap. The hand multiset was identical to the pre-discard
/// hand, so TryFindAddedTile could not see the new draw, and the hand-commit
/// block (ownDiscardAwaitingHandCommit) never released. BuildBatch returned
/// events=0 forever on the player's own discard turn.
/// </summary>
public class SameTileRedrawRegressionTests
{
    private static readonly Tile EightSou = Tile.FromId(25); // 8s

    private static StateSnapshot BuildTurnState(int wall) => StateSnapshot.Empty with
    {
        OurSeat = 0,
        // 13 distinct tiles + 8s appended last (the reader appends the newest
        // draw at the end of the hand array).
        Hand = Enumerable.Range(0, 13).Select(Tile.FromId).Append(EightSou).ToArray(),
        DoraIndicators = [Tile.FromId(16)],
        WallRemaining = wall,
        Legal = new LegalActions(ActionFlags.Discard, [], [], [], []),
    };

    [Fact]
    public void Redrawing_the_discarded_tile_still_emits_the_new_tsumo()
    {
        var tracker = new MjaiSessionTracker();

        // Turn 1: actionable 14-tile hand opens the session.
        StateSnapshot turn1 = BuildTurnState(wall: 52);
        MjaiEventBatch opening = tracker.BuildBatch(turn1);
        Assert.True(opening.EventCount > 0);
        tracker.NoteBatchSent(opening.Json);

        // Echo frame: the river already shows our 8s discard while the
        // concealed hand array is still the stale pre-discard 14 tiles
        // (EMJ publishes the river entry before the hand commit).
        SeatView[] seats = turn1.Seats.ToArray();
        seats[0] = seats[0] with
        {
            Discards = [EightSou],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        StateSnapshot echoFrame = turn1 with { Seats = seats };
        MjaiEventBatch echo = tracker.BuildBatch(echoFrame);
        JsonArray echoEvents = JsonNode.Parse(echo.Json)!.AsArray();
        Assert.Contains(echoEvents, e =>
            e!.AsObject()["type"]!.GetValue<string>() == "dahai"
            && e!.AsObject()["actor"]!.GetValue<int>() == 0);
        tracker.NoteBatchSent(echo.Json);

        // Turn 2: we drew another 8s. The hand multiset is identical to the
        // pre-discard hand, the 13-tile frame was never polled, and the wall
        // dropped by 3 (one full go-around). Before the fix this produced
        // events=0 forever; the wall evidence must release the commit block
        // and emit the new tsumo.
        StateSnapshot turn2 = echoFrame with { WallRemaining = 49 };
        MjaiEventBatch decision = tracker.BuildBatch(turn2);

        Assert.True(decision.EventCount > 0);
        JsonArray events = JsonNode.Parse(decision.Json)!.AsArray();
        JsonObject last = events[^1]!.AsObject();
        Assert.Equal("tsumo", last["type"]!.GetValue<string>());
        Assert.Equal(0, last["actor"]!.GetValue<int>());
        Assert.Equal("8s", last["pai"]!.GetValue<string>());
        Assert.True(ExternalMjaiProcess.BatchExpectsDecision(decision.Json, turn2.OurSeat));
    }

    [Fact]
    public void Stale_pre_discard_frame_in_the_same_turn_is_not_a_draw()
    {
        var tracker = new MjaiSessionTracker();

        StateSnapshot turn1 = BuildTurnState(wall: 52);
        MjaiEventBatch opening = tracker.BuildBatch(turn1);
        tracker.NoteBatchSent(opening.Json);

        SeatView[] seats = turn1.Seats.ToArray();
        seats[0] = seats[0] with
        {
            Discards = [EightSou],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        StateSnapshot echoFrame = turn1 with { Seats = seats };
        MjaiEventBatch echo = tracker.BuildBatch(echoFrame);
        tracker.NoteBatchSent(echo.Json);

        // Same-turn stale frame: the wall moved by at most one draw and the
        // hand array is unchanged. Emitting a tsumo here would duplicate the
        // draw and eventually overflow the engine's private-hand limit.
        StateSnapshot staleFrame = echoFrame with { WallRemaining = 51 };
        MjaiEventBatch stale = tracker.BuildBatch(staleFrame);

        if (stale.EventCount > 0)
        {
            JsonArray events = JsonNode.Parse(stale.Json)!.AsArray();
            Assert.DoesNotContain(events, e =>
                e!.AsObject()["type"]!.GetValue<string>() == "tsumo"
                && e!.AsObject()["actor"]!.GetValue<int>() == 0);
        }
    }
}
