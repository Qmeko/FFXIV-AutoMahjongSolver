using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;
using Xunit;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class PublicRiverMergerTests
{
    private static Tile T(string name) => Tiles.Parse(name)[0];

    [Fact]
    public void Complete_atk_tail_wins()
    {
        var existing = new[] { T("1m"), T("2m") };
        var atk = new[] { T("1m"), T("2m"), T("3m") };
        var atkTedashi = new[] { true, false, true };

        var result = PublicRiverMerger.Merge(3, existing, [], atk, atkTedashi);

        Assert.True(result.Complete);
        Assert.Equal(atk, result.Discards);
        Assert.Equal(atkTedashi, result.DiscardIsTedashi);
    }

    [Fact]
    public void Visual_prefix_plus_atk_suffix_are_concatenated()
    {
        var visual = new[] { T("1p"), T("2p"), T("3p"), T("4p") };
        var atk = new[] { T("5p"), T("6p") }; // mid-hand probe tail

        var result = PublicRiverMerger.Merge(6, visual, [], atk, [true, false]);

        Assert.True(result.Complete);
        Assert.Equal(new[] { T("1p"), T("2p"), T("3p"), T("4p"), T("5p"), T("6p") }, result.Discards);
        Assert.False(result.DiscardIsTedashi[5]);
    }

    [Fact]
    public void Overlapping_atk_suffix_does_not_duplicate_tiles()
    {
        var visual = new[] { T("1s"), T("2s"), T("3s"), T("4s") };
        var atk = new[] { T("3s"), T("4s"), T("5s") };

        var result = PublicRiverMerger.Merge(5, visual, [], atk, [true, true, false]);

        Assert.True(result.Complete);
        Assert.Equal(new[] { T("1s"), T("2s"), T("3s"), T("4s"), T("5s") }, result.Discards);
    }

    [Fact]
    public void Complete_visual_keeps_tiles_and_overlays_matching_atk_tedashi()
    {
        var visual = new[] { T("1m"), T("2m"), T("3m") };
        var atk = new[] { T("2m"), T("3m") };

        var result = PublicRiverMerger.Merge(3, visual, [true, true, true], atk, [false, true]);

        Assert.True(result.Complete);
        Assert.Equal(visual, result.Discards);
        Assert.True(result.DiscardIsTedashi[0]);
        Assert.False(result.DiscardIsTedashi[1]);
        Assert.True(result.DiscardIsTedashi[2]);
    }

    [Fact]
    public void Partial_best_effort_concatenates_non_overlapping_sides()
    {
        var visual = new[] { T("1m"), T("2m") };
        var atk = new[] { T("9m") };

        var result = PublicRiverMerger.Merge(5, visual, [], atk, [true]);

        Assert.False(result.Complete);
        Assert.Equal(new[] { T("1m"), T("2m"), T("9m") }, result.Discards);
    }

    [Fact]
    public void Empty_atk_keeps_partial_visual()
    {
        var visual = new[] { T("1m"), T("2m") };

        var result = PublicRiverMerger.Merge(5, visual, [], [], []);

        Assert.False(result.Complete);
        Assert.Equal(visual, result.Discards);
    }
}
