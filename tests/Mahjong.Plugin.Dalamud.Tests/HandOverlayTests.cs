using System.Numerics;
using Mahjong.Plugin.Dalamud.UI;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class HandOverlayTests
{
    [Fact]
    public void PickHandRow_DeduplicatesFrameAndImageLayers()
    {
        var candidates = new List<(Vector2 Pos, Vector2 Size)>();
        for (int i = 0; i < 14; i++)
            candidates.Add((new Vector2(100 + i * 42, 500), new Vector2(38, 62)));

        candidates.Add((new Vector2(100 + 6 * 42 + 2, 502), new Vector2(34, 58)));

        var row = HandOverlay.PickHandRowFromCandidates(candidates, 14);

        Assert.NotNull(row);
        Assert.Equal(14, row.Count);
        Assert.Equal(100 + 6 * 42, row[6].Pos.X);
        Assert.Equal(100 + 7 * 42, row[7].Pos.X);
    }

    [Fact]
    public void Deduplicate_DoesNotMergeAdjacentTiles()
    {
        var candidates = new List<(Vector2 Pos, Vector2 Size)>
        {
            (new Vector2(100, 500), new Vector2(40, 64)),
            (new Vector2(142, 500), new Vector2(40, 64)),
        };

        var result = HandOverlay.DeduplicateOverlappingRects(candidates);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void PickHandRow_ExcludesSameRowMeldGroupAfterPon()
    {
        var candidates = new List<(Vector2 Pos, Vector2 Size)>();
        for (int i = 0; i < 11; i++)
            candidates.Add((new Vector2(700 + i * 42, 500), new Vector2(38, 62)));
        for (int i = 0; i < 3; i++)
            candidates.Add((new Vector2(1250 + i * 42, 500), new Vector2(38, 62)));

        var row = HandOverlay.PickHandRowFromCandidates(candidates, 11);

        Assert.NotNull(row);
        Assert.Equal(11, row.Count);
        Assert.Equal(700, row[0].Pos.X);
        Assert.Equal(700 + 10 * 42, row[10].Pos.X);
    }
}
