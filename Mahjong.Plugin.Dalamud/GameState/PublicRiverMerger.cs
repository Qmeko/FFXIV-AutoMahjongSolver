using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Core;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>
/// Combines a visual (or previously decoded) river prefix with a possibly
/// shorter AtkValue event tail so mid-hand plugin loads still expose as many
/// public discard tiles as possible to Mortal.
/// </summary>
internal static class PublicRiverMerger
{
    public readonly record struct Result(
        IReadOnlyList<Tile> Discards,
        IReadOnlyList<bool> DiscardIsTedashi,
        bool Complete);

    public static Result Merge(
        int expectedCount,
        IReadOnlyList<Tile> existing,
        IReadOnlyList<bool> existingTedashi,
        IReadOnlyList<Tile> atkTail,
        IReadOnlyList<bool> atkTedashi)
    {
        if (expectedCount <= 0)
            return new Result([], [], Complete: true);

        existing ??= [];
        existingTedashi ??= [];
        atkTail ??= [];
        atkTedashi ??= [];

        if (atkTail.Count == expectedCount)
        {
            return new Result(
                atkTail.ToArray(),
                NormalizeTedashi(atkTedashi, expectedCount),
                Complete: true);
        }

        if (existing.Count == expectedCount)
        {
            var tedashi = NormalizeTedashi(existingTedashi, expectedCount).ToArray();
            if (TryOverlayAtkTedashi(existing, tedashi, atkTail, atkTedashi))
                return new Result(existing.ToArray(), tedashi, Complete: true);
            return new Result(existing.ToArray(), tedashi, Complete: true);
        }

        if (existing.Count > 0 && atkTail.Count > 0)
        {
            int overlap = LongestSuffixPrefixOverlap(existing, atkTail);
            var tiles = new List<Tile>(existing.Count + atkTail.Count);
            tiles.AddRange(existing);
            for (int i = overlap; i < atkTail.Count && tiles.Count < expectedCount; i++)
                tiles.Add(atkTail[i]);

            if (tiles.Count > existing.Count)
            {
                if (tiles.Count > expectedCount)
                    tiles.RemoveRange(expectedCount, tiles.Count - expectedCount);

                var tedashi = NormalizeTedashi(existingTedashi, existing.Count).ToList();
                while (tedashi.Count < tiles.Count)
                {
                    int atkIndex = overlap + (tedashi.Count - existing.Count);
                    tedashi.Add(atkIndex >= 0 && atkIndex < atkTedashi.Count ? atkTedashi[atkIndex] : true);
                }

                return new Result(tiles.ToArray(), tedashi.ToArray(), Complete: tiles.Count == expectedCount);
            }
        }

        if (existing.Count >= atkTail.Count && existing.Count > 0)
        {
            var clipped = existing.Count > expectedCount
                ? existing.Take(expectedCount).ToArray()
                : existing.ToArray();
            return new Result(
                clipped,
                NormalizeTedashi(existingTedashi, clipped.Length),
                Complete: clipped.Length == expectedCount);
        }

        if (atkTail.Count > 0)
        {
            var clipped = atkTail.Count > expectedCount
                ? atkTail.Take(expectedCount).ToArray()
                : atkTail.ToArray();
            return new Result(
                clipped,
                NormalizeTedashi(atkTedashi, clipped.Length),
                Complete: clipped.Length == expectedCount);
        }

        return new Result([], [], Complete: false);
    }

    internal static int LongestSuffixPrefixOverlap(IReadOnlyList<Tile> left, IReadOnlyList<Tile> right)
    {
        int max = Math.Min(left.Count, right.Count);
        for (int len = max; len > 0; len--)
        {
            bool match = true;
            for (int i = 0; i < len; i++)
            {
                if (left[left.Count - len + i].Id != right[i].Id)
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return len;
        }
        return 0;
    }

    private static bool TryOverlayAtkTedashi(
        IReadOnlyList<Tile> complete,
        bool[] tedashi,
        IReadOnlyList<Tile> atkTail,
        IReadOnlyList<bool> atkTedashi)
    {
        if (atkTail.Count == 0 || atkTail.Count >= complete.Count)
            return false;

        int start = complete.Count - atkTail.Count;
        for (int i = 0; i < atkTail.Count; i++)
        {
            if (complete[start + i].Id != atkTail[i].Id)
                return false;
        }

        for (int i = 0; i < atkTail.Count; i++)
            tedashi[start + i] = i < atkTedashi.Count ? atkTedashi[i] : true;
        return true;
    }

    private static bool[] NormalizeTedashi(IReadOnlyList<bool> source, int length)
    {
        var result = new bool[length];
        for (int i = 0; i < length; i++)
            result[i] = i < source.Count ? source[i] : true;
        return result;
    }
}
