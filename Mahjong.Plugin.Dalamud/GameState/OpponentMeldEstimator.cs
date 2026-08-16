using System.Collections.Generic;
using Mahjong.Core;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>
/// Approximates open melds from a seat's non-river face-up tile images.
/// Claimed-from seat is unknown from the visual tree, so it is left as -1.
/// </summary>
internal static class OpponentMeldEstimator
{
    public static IReadOnlyList<Meld> Estimate(IReadOnlyList<Tile> tiles)
    {
        if (tiles is null || tiles.Count < 3)
            return [];

        var counts = new int[Tile.Count34];
        foreach (var tile in tiles)
        {
            if ((uint)tile.Id < Tile.Count34)
                counts[tile.Id]++;
        }

        var melds = new List<Meld>(4);

        for (int id = 0; id < Tile.Count34; id++)
        {
            while (counts[id] >= 4)
            {
                counts[id] -= 4;
                var kind = Tile.FromId(id);
                // Visual ankan/minkan distinction is unreliable; treat as open kan.
                melds.Add(Meld.MinKan(kind, kind, fromSeat: -1));
            }
        }

        for (int id = 0; id < Tile.Count34; id++)
        {
            while (counts[id] >= 3)
            {
                counts[id] -= 3;
                var kind = Tile.FromId(id);
                melds.Add(Meld.Pon(kind, kind, fromSeat: -1));
            }
        }

        for (int suit = 0; suit < 3; suit++)
        {
            int suitBase = suit * 9;
            int n = 0;
            while (n <= 6)
            {
                int a = suitBase + n;
                int b = a + 1;
                int c = a + 2;
                if (counts[a] > 0 && counts[b] > 0 && counts[c] > 0)
                {
                    counts[a]--;
                    counts[b]--;
                    counts[c]--;
                    var low = Tile.FromId(a);
                    // Claimed tile is unknown; use the low tile as a stable placeholder.
                    melds.Add(Meld.Chi(low, low, fromSeat: -1));
                }
                else
                {
                    n++;
                }
            }
        }

        return melds;
    }
}
