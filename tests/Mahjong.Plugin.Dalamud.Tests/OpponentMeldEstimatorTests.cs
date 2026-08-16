using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;
using Xunit;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class OpponentMeldEstimatorTests
{
    [Fact]
    public void Estimates_pon_from_three_identical_tiles()
    {
        var tiles = Tiles.Parse("555m");
        var melds = OpponentMeldEstimator.Estimate(tiles);

        Assert.Single(melds);
        Assert.Equal(MeldKind.Pon, melds[0].Kind);
        Assert.Equal(Tile.FromId(4), melds[0].Tiles[0]);
        Assert.Equal(-1, melds[0].ClaimedFromSeat);
    }

    [Fact]
    public void Estimates_chi_from_sequence()
    {
        var tiles = Tiles.Parse("123p");
        var melds = OpponentMeldEstimator.Estimate(tiles);

        Assert.Single(melds);
        Assert.Equal(MeldKind.Chi, melds[0].Kind);
        Assert.Equal(Tile.FromId(9), melds[0].Tiles[0]);
    }

    [Fact]
    public void Estimates_kan_before_pon_when_four_identical()
    {
        var tiles = Tiles.Parse("1111z");
        var melds = OpponentMeldEstimator.Estimate(tiles);

        Assert.Single(melds);
        Assert.Equal(MeldKind.MinKan, melds[0].Kind);
    }

    [Fact]
    public void Estimates_multiple_melds()
    {
        var tiles = Tiles.Parse("333m789s");
        var melds = OpponentMeldEstimator.Estimate(tiles);

        Assert.Equal(2, melds.Count);
        Assert.Contains(melds, m => m.Kind == MeldKind.Pon);
        Assert.Contains(melds, m => m.Kind == MeldKind.Chi);
    }

    [Fact]
    public void Returns_empty_when_fewer_than_three_tiles()
    {
        Assert.Empty(OpponentMeldEstimator.Estimate(Tiles.Parse("12m")));
    }
}
