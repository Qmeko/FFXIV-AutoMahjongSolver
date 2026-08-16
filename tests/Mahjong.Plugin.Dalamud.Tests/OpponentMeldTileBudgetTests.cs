using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;
using Xunit;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class OpponentMeldTileBudgetTests
{
    [Fact]
    public void Rejects_pon_when_rivers_already_show_two_copies()
    {
        Tile twoPin = Tile.FromId(10); // 2p
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[1] = seats[1] with
        {
            Discards = [twoPin, twoPin],
            DiscardCount = 2,
        };
        seats[2] = seats[2] with
        {
            Discards = [twoPin],
            DiscardCount = 1,
        };
        var snapshot = StateSnapshot.Empty with
        {
            Hand = Enumerable.Range(0, 13).Select(Tile.FromId).ToArray(),
            Seats = seats,
        };

        var estimated = new[] { Meld.Pon(twoPin, twoPin, fromSeat: -1) };
        var kept = OpponentMeldTileBudget.FilterValidPrefix(estimated, snapshot, seat: 1);

        Assert.Empty(kept);
        Assert.True(OpponentMeldTileBudget.MeldExceedsBudget(estimated[0], snapshot, seat: 1));
    }

    [Fact]
    public void Keeps_pon_when_visible_copies_fit_in_budget()
    {
        Tile fiveMan = Tile.FromId(4); // 5m
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = [fiveMan],
            DiscardCount = 1,
        };
        var snapshot = StateSnapshot.Empty with
        {
            Hand = Enumerable.Range(0, 13).Select(i => Tile.FromId((i + 10) % Tile.Count34)).ToArray(),
            Seats = seats,
        };

        var estimated = new[] { Meld.Pon(fiveMan, fiveMan, fromSeat: -1) };
        var kept = OpponentMeldTileBudget.FilterValidPrefix(estimated, snapshot, seat: 1);

        Assert.Single(kept);
        Assert.False(OpponentMeldTileBudget.MeldExceedsBudget(estimated[0], snapshot, seat: 1));
    }

    [Fact]
    public void Keeps_only_prefix_melds_that_fit()
    {
        Tile threeMan = Tile.FromId(2); // 3m
        Tile twoPin = Tile.FromId(10); // 2p
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[0] = seats[0] with
        {
            Discards = [twoPin, twoPin],
            DiscardCount = 2,
        };
        seats[2] = seats[2] with
        {
            Discards = [twoPin],
            DiscardCount = 1,
        };
        var snapshot = StateSnapshot.Empty with
        {
            Hand = [Tile.FromId(0), Tile.FromId(1), Tile.FromId(3)],
            Seats = seats,
        };

        var estimated = new[]
        {
            Meld.Pon(threeMan, threeMan, fromSeat: -1),
            Meld.Pon(twoPin, twoPin, fromSeat: -1),
        };
        var kept = OpponentMeldTileBudget.FilterValidPrefix(estimated, snapshot, seat: 1);

        Assert.Single(kept);
        Assert.Equal(MeldKind.Pon, kept[0].Kind);
        Assert.Equal(threeMan, kept[0].Tiles[0]);
    }
}
