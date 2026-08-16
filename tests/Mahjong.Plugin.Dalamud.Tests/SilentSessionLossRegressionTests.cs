using System.Linq;
using System.Text.Json.Nodes;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Xunit;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Regression tests for the 2026-08-01 19:53-19:54 field incident chain:
/// 1. A mid-turn round resync dropped the already-drawn 14th tile from
///    start_kyoku without a tsumo, so the following own dahai referenced a
///    tile the engine never saw ("rule violation: attempt to discard 1p from
///    void") and poisoned the session - twice within 15 seconds.
/// 2. After the watchdog restart, an aborted stateless resync left
///    gameStarted=true without ever sending start_game. The fresh engine
///    silently ignores all events before start_game and answers none with no
///    stderr, so the watchdog stayed blind and the user needed a manual
///    resync to get instructions back.
/// 3. The post-restart bootstrap replayed our own historical turns as "draw
///    the current last hand tile, discard a tile from long ago" - another
///    discard-from-void generator.
/// </summary>
public class SilentSessionLossRegressionTests
{
    private static Tile[] Hand14() => Enumerable.Range(0, 14).Select(Tile.FromId).ToArray();

    private static StateSnapshot DrawState(Tile[] hand14, int wall) => StateSnapshot.Empty with
    {
        OurSeat = 0,
        Hand = hand14,
        WallRemaining = wall,
        DoraIndicators = [Tile.FromId(19)],
        Legal = new LegalActions(ActionFlags.Discard, hand14, [], [], []),
        AddonStateCode = 6,
    };

    private static StateSnapshot WithCountOnlyBacklog(StateSnapshot state, int seat, int discardCount)
    {
        SeatView[] seats = state.Seats.ToArray();
        seats[seat] = seats[seat] with { DiscardCount = discardCount };
        return state with { Seats = seats };
    }

    private static MjaiSessionTracker StartedTracker(out StateSnapshot initial)
    {
        var tracker = new MjaiSessionTracker();
        initial = DrawState(Hand14(), wall: 68);
        MjaiEventBatch opening = tracker.BuildBatch(initial);
        Assert.True(opening.StartsGame);
        tracker.NoteBatchSent(opening.Json);
        return tracker;
    }

    [Fact]
    public void Round_resync_replays_the_committed_draw_as_tsumo()
    {
        MjaiSessionTracker tracker = StartedTracker(out StateSnapshot initial);

        // Two discards appear at once for seat 2 with no decoded tiles: the
        // unordered backlog forces a round resync while we hold 14 tiles.
        StateSnapshot backlog = WithCountOnlyBacklog(initial, seat: 2, discardCount: 2)
            with
        { WallRemaining = 66 };
        MjaiEventBatch resync = tracker.BuildBatch(backlog);

        JsonArray events = JsonNode.Parse(resync.Json)!.AsArray();
        Assert.Equal(3, events.Count);
        Assert.Equal("end_kyoku", events[0]!["type"]!.GetValue<string>());
        Assert.Equal("start_kyoku", events[1]!["type"]!.GetValue<string>());
        Assert.Equal(13, events[1]!["tehais"]!.AsArray()[0]!.AsArray().Count);
        Assert.Equal("tsumo", events[2]!["type"]!.GetValue<string>());
        Assert.Equal(0, events[2]!["actor"]!.GetValue<int>());
        Assert.Equal(
            MjaiJson.EncodeTile(initial.Hand[^1]),
            events[2]!["pai"]!.GetValue<string>());
    }

    [Fact]
    public void Round_resync_waits_for_the_discard_surface_and_retries()
    {
        MjaiSessionTracker tracker = StartedTracker(out StateSnapshot initial);

        // The backlog arrives on a transient frame: 14 tiles are visible but
        // the Discard surface is not published yet. No batch may be emitted.
        StateSnapshot transientBacklog = WithCountOnlyBacklog(initial, seat: 2, discardCount: 2)
            with
        {
            WallRemaining = 66,
            Legal = LegalActions.None,
        };
        Assert.Equal(0, tracker.BuildBatch(transientBacklog).EventCount);

        // Once the surface confirms our turn, the resync fires with the
        // committed draw instead of being silently absorbed.
        StateSnapshot actionable = transientBacklog with
        {
            Legal = new LegalActions(ActionFlags.Discard, transientBacklog.Hand, [], [], []),
        };
        MjaiEventBatch resync = tracker.BuildBatch(actionable);

        JsonArray events = JsonNode.Parse(resync.Json)!.AsArray();
        Assert.Equal("end_kyoku", events[0]!["type"]!.GetValue<string>());
        Assert.Equal("tsumo", events[^1]!["type"]!.GetValue<string>());
        Assert.Equal(
            MjaiJson.EncodeTile(actionable.Hand[^1]),
            events[^1]!["pai"]!.GetValue<string>());
    }

    [Fact]
    public void Aborted_stateless_resync_does_not_leak_start_game()
    {
        var tracker = new MjaiSessionTracker();

        // Restart mid-hand on a Riichi/Pass surface: 13 tiles, no concrete
        // draw or call tile. The stateless resync must abort without leaving
        // a phantom "start_game already sent" state behind.
        Tile[] hand13 = Enumerable.Range(0, 13).Select(Tile.FromId).ToArray();
        StateSnapshot riichiSurface = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand13,
            WallRemaining = 22,
            DoraIndicators = [Tile.FromId(19)],
            Legal = new LegalActions(ActionFlags.Riichi | ActionFlags.Pass, [], [], [], []),
            AddonStateCode = 6,
        };
        StateSnapshot withHistory = WithCountOnlyBacklog(riichiSurface, seat: 1, discardCount: 3);
        Assert.Equal(0, tracker.BuildBatch(withHistory).EventCount);

        // The next own draw must still open the engine session with
        // start_game; a fresh engine silently ignores everything before it.
        Tile[] hand14 = hand13.Append(Tile.FromId(20)).ToArray();
        StateSnapshot draw = withHistory with
        {
            Hand = hand14,
            WallRemaining = 21,
            Legal = new LegalActions(ActionFlags.Discard, hand14, [], [], []),
        };
        MjaiEventBatch batch = tracker.BuildBatch(draw);

        Assert.True(batch.StartsGame);
        JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();
        Assert.Equal("start_game", events[0]!["type"]!.GetValue<string>());
        Assert.Equal("tsumo", events[^1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Bootstrap_history_replays_own_turns_with_consistent_tiles()
    {
        var tracker = new MjaiSessionTracker();
        StateSnapshot endOfPreviousHand = DrawState(Hand14(), wall: 20);
        MjaiEventBatch opening = tracker.BuildBatch(endOfPreviousHand);
        Assert.True(opening.StartsGame);
        tracker.NoteBatchSent(opening.Json);

        // Wall replenishment marks a new hand observed one turn late: every
        // river (including ours) already carries the opening discard. Our own
        // history must never invent a draw that differs from the discard it
        // explains.
        Tile ownFirst = Tile.FromId(30);
        Tile[] hand14 = Hand14();
        StateSnapshot newHand = DrawState(hand14, wall: 69);
        SeatView[] seats = newHand.Seats.ToArray();
        seats[0] = seats[0] with
        {
            Discards = [ownFirst],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        seats[1] = seats[1] with
        {
            Discards = [Tile.FromId(5)],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        newHand = newHand with { Seats = seats };

        MjaiEventBatch batch = tracker.BuildBatch(newHand);
        JsonArray events = JsonNode.Parse(batch.Json)!.AsArray();

        var ownHistory = events.OfType<JsonObject>()
            .Where(evt => evt["type"]?.GetValue<string>() == "dahai"
                && evt["actor"]?.GetValue<int>() == 0)
            .ToList();
        JsonObject single = Assert.Single(ownHistory);
        Assert.Equal(MjaiJson.EncodeTile(ownFirst), single["pai"]!.GetValue<string>());
        foreach (JsonObject dahai in ownHistory)
        {
            int index = events.IndexOf(dahai);
            JsonObject draw = (JsonObject)events[index - 1]!;
            Assert.Equal("tsumo", draw["type"]!.GetValue<string>());
            Assert.Equal(0, draw["actor"]!.GetValue<int>());
            Assert.Equal(
                dahai["pai"]!.GetValue<string>(),
                draw["pai"]!.GetValue<string>());
        }

        // The current decision draw still closes the batch.
        Assert.Equal("tsumo", events[^1]!["type"]!.GetValue<string>());
        Assert.Equal(
            MjaiJson.EncodeTile(hand14[^1]),
            events[^1]!["pai"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""[{"type":"tsumo","actor":0,"pai":"5m"}]""", 0, true)]
    [InlineData("""[{"type":"start_kyoku"},{"type":"tsumo","actor":0,"pai":"5m"}]""", 0, true)]
    [InlineData("""[{"type":"tsumo","actor":1,"pai":"?"},{"type":"dahai","actor":1,"pai":"5m","tsumogiri":true}]""", 0, false)]
    [InlineData("""[{"type":"tsumo","actor":0,"pai":"5m"},{"type":"dahai","actor":0,"pai":"5m","tsumogiri":true}]""", 0, false)]
    [InlineData("""[]""", 0, false)]
    public void Batch_ends_with_own_draw_detects_discard_class_decisions(string json, int seat, bool expected)
    {
        Assert.Equal(expected, ExternalMjaiProcess.BatchEndsWithOwnDraw(json, seat));
    }
}
