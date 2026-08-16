using System.Collections.Generic;
using Mahjong.Core;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>
/// Rejects visually estimated opponent melds that would invent a fifth copy of any
/// tile kind (Mortal then crashes with "attempt to witness the fifth …").
/// </summary>
internal static class OpponentMeldTileBudget
{
    public static IReadOnlyList<Meld> FilterValidPrefix(
        IReadOnlyList<Meld> estimated,
        StateSnapshot snapshot,
        int seat)
    {
        if (estimated is null || estimated.Count == 0)
            return [];

        var accepted = new List<Meld>(estimated.Count);
        for (int i = 0; i < estimated.Count; i++)
        {
            accepted.Add(estimated[i]);
            if (ExceedsBudget(snapshot, seat, accepted))
            {
                accepted.RemoveAt(accepted.Count - 1);
                break;
            }
        }

        return accepted;
    }

    /// <summary>
    /// True when emitting <paramref name="meld"/> for <paramref name="seat"/> would
    /// make any tile kind appear more than four times in the public snapshot.
    /// </summary>
    public static bool MeldExceedsBudget(Meld meld, StateSnapshot snapshot, int seat)
    {
        IReadOnlyList<Meld> current = seat >= 0 && seat < snapshot.Seats.Count
            ? snapshot.Seats[seat].Melds
            : [];

        var prefix = new List<Meld>(current.Count + 1);
        bool found = false;
        for (int i = 0; i < current.Count; i++)
        {
            prefix.Add(current[i]);
            if (SameMeldIdentity(current[i], meld))
            {
                found = true;
                break;
            }
        }

        if (!found)
            prefix.Add(meld);

        return ExceedsBudget(snapshot, seat, prefix);
    }

    private static bool ExceedsBudget(
        StateSnapshot snapshot,
        int seat,
        IReadOnlyList<Meld> seatMelds)
    {
        var counts = new int[Tile.Count34];
        AddTiles(counts, snapshot.Hand);
        AddTiles(counts, snapshot.DoraIndicators);
        AddTiles(counts, snapshot.UraDoraIndicators);
        AddMelds(counts, snapshot.OurMelds);

        int ourSeat = snapshot.OurSeat;
        for (int s = 0; s < snapshot.Seats.Count && s < 4; s++)
        {
            SeatView view = snapshot.Seats[s];
            AddTiles(counts, view.Discards);
            if (s == seat)
                continue;
            if (s == ourSeat && snapshot.OurMelds.Count > 0)
                continue;
            AddMelds(counts, view.Melds);
        }

        AddMelds(counts, seatMelds);

        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 4)
                return true;
        }

        return false;
    }

    private static bool SameMeldIdentity(Meld left, Meld right)
    {
        if (left.Kind != right.Kind || left.Tiles.Length != right.Tiles.Length)
            return false;
        for (int i = 0; i < left.Tiles.Length; i++)
        {
            if (left.Tiles[i].Id != right.Tiles[i].Id)
                return false;
        }

        return true;
    }

    private static void AddTiles(int[] counts, IReadOnlyList<Tile> tiles)
    {
        for (int i = 0; i < tiles.Count; i++)
        {
            int id = tiles[i].Id;
            if ((uint)id < Tile.Count34)
                counts[id]++;
        }
    }

    private static void AddMelds(int[] counts, IReadOnlyList<Meld> melds)
    {
        for (int i = 0; i < melds.Count; i++)
            AddTiles(counts, melds[i].Tiles);
    }
}
