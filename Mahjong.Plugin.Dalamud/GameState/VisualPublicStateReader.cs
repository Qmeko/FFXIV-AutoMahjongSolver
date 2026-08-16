using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Core;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>
/// Reads public table information from the visible Emj node tree. Memory offsets remain the
/// authoritative source; visual data only fills fields that are otherwise missing.
/// </summary>
internal static unsafe class VisualPublicStateReader
{
    private readonly record struct VisualTile(Tile Tile, float X, float Y, float Rotation, uint NodeId, float Radius);
    private static string? lastDiagnostic;
    private static readonly List<Tile>[] CachedRivers = [[], [], [], []];
    private static readonly int[] CachedCounts = [-1, -1, -1, -1];
    private static readonly bool[] CachedRiichi = [false, false, false, false];
    private static readonly int[] CachedRiichiIndex = [-1, -1, -1, -1];
    private static readonly List<Meld>[] CachedMelds = [[], [], [], []];

    public static StateSnapshot Apply(AtkUnitBase* unit, StateSnapshot snapshot, IPluginLog log)
    {
        if (unit == null || unit->RootNode == null || snapshot.Seats.Count != 4)
            return snapshot;

        var tiles = ReadVisibleTiles(unit);
        if (tiles.Count == 0)
        {
            EmitDiagnostic(log, "no decodable tile image nodes");
            return snapshot;
        }

        float width = unit->RootNode->Width;
        float height = unit->RootNode->Height;
        if (width <= 0 || height <= 0)
            return snapshot;

        float cx = width * 0.5f;
        float cy = height * 0.5f;
        float minDim = MathF.Min(width, height);
        float minR = minDim * 0.075f;
        float riverMaxR = minDim * 0.47f;
        float meldMaxR = minDim * 0.58f;

        var riverBySeat = new List<VisualTile>[4] { [], [], [], [] };
        var meldBySeat = new List<VisualTile>[4] { [], [], [], [] };
        foreach (var tile in tiles)
        {
            float dx = tile.X - cx;
            float dy = tile.Y - cy;
            float r = MathF.Sqrt(dx * dx + dy * dy);
            if (r < minR || r > meldMaxR)
                continue;

            int seat;
            if (MathF.Abs(dx) > MathF.Abs(dy))
                seat = dx > 0 ? 1 : 3;
            else
                seat = dy > 0 ? 0 : 2;

            var tagged = tile with { Radius = r };
            if (r <= riverMaxR)
                riverBySeat[seat].Add(tagged);
            else
                meldBySeat[seat].Add(tagged);
        }

        var seats = snapshot.Seats.ToArray();
        bool changed = false;
        var diag = new List<string>(4);
        for (int seat = 0; seat < 4; seat++)
        {
            int expected = seats[seat].DiscardCount;
            if (expected <= 0)
            {
                CachedRivers[seat].Clear();
                CachedCounts[seat] = 0;
                CachedRiichi[seat] = false;
                CachedRiichiIndex[seat] = -1;
                CachedMelds[seat].Clear();
                continue;
            }

            var orderedVisual = OrderRiver(riverBySeat[seat], seat).Take(expected).ToArray();
            var ordered = orderedVisual.Select(v => v.Tile).ToArray();
            bool completeVisual = ordered.Length == expected;

            if (completeVisual)
            {
                CachedRivers[seat].Clear();
                CachedRivers[seat].AddRange(ordered);
                CachedCounts[seat] = expected;
            }
            else if (CachedCounts[seat] == expected && CachedRivers[seat].Count == expected)
            {
                ordered = CachedRivers[seat].ToArray();
                completeVisual = true;
            }
            else if (ordered.Length > 0)
            {
                // Keep the longest partial decode so AtkValue can append its tail.
                if (ordered.Length >= CachedRivers[seat].Count || CachedCounts[seat] != expected)
                {
                    CachedRivers[seat].Clear();
                    CachedRivers[seat].AddRange(ordered);
                    CachedCounts[seat] = expected;
                }
                else
                {
                    ordered = CachedRivers[seat].ToArray();
                }
            }
            else if (CachedCounts[seat] == expected && CachedRivers[seat].Count > 0)
            {
                ordered = CachedRivers[seat].ToArray();
            }

            diag.Add($"s{seat}:expected={expected},riverCand={riverBySeat[seat].Count},decoded={orderedVisual.Length},used={ordered.Length},cache={(completeVisual ? "refresh" : CachedCounts[seat] == expected ? "hit" : "miss")}");

            if (ordered.Length > 0
                && (ordered.Length > seats[seat].Discards.Count
                    || (ordered.Length == expected && seats[seat].Discards.Count != expected)))
            {
                seats[seat] = seats[seat] with
                {
                    Discards = ordered,
                    DiscardIsTedashi = Enumerable.Repeat(true, ordered.Length).ToArray(),
                };
                changed = true;
            }

            // A riichi declaration tile is displayed sideways. Rotation is reported in radians
            // on current clients; tolerate degree-style values used by older structs.
            int rotated = Array.FindIndex(orderedVisual, v => IsSideways(v.Rotation));
            if (rotated >= 0)
            {
                CachedRiichi[seat] = true;
                CachedRiichiIndex[seat] = rotated;
            }

            if (CachedRiichi[seat] && !seats[seat].Riichi)
            {
                int index = CachedRiichiIndex[seat];
                seats[seat] = seats[seat] with
                {
                    Riichi = true,
                    RiichiDiscardIndex = index,
                    Ippatsu = index >= 0 && index == expected - 1,
                };
                changed = true;
            }

            // Seat 0 melds are owned by MeldTracker / OurMelds. Only estimate opponents.
            if (seat == 0)
                continue;

            // Use only the outer meld band. River leftovers after Take(expected)
            // are often still discard tiles and invent illegal fifth copies.
            var budgetSnapshot = snapshot with { Seats = seats };
            var meldTiles = meldBySeat[seat].Select(v => v.Tile).ToArray();
            var estimated = OpponentMeldTileBudget.FilterValidPrefix(
                OpponentMeldEstimator.Estimate(meldTiles),
                budgetSnapshot,
                seat);
            if (estimated.Count > 0)
            {
                CachedMelds[seat].Clear();
                CachedMelds[seat].AddRange(estimated);
            }
            else if (CachedMelds[seat].Count > 0)
            {
                var kept = OpponentMeldTileBudget.FilterValidPrefix(
                    CachedMelds[seat],
                    budgetSnapshot,
                    seat);
                CachedMelds[seat].Clear();
                CachedMelds[seat].AddRange(kept);
                estimated = kept;
            }

            if (estimated.Count > 0
                && (seats[seat].Melds.Count == 0 || estimated.Count >= seats[seat].Melds.Count))
            {
                seats[seat] = seats[seat] with { Melds = estimated };
                changed = true;
                diag[^1] += $",melds={estimated.Count}";
            }
        }

        EmitDiagnostic(log, string.Join(";", diag));
        return changed ? snapshot with { Seats = seats } : snapshot;
    }

    private static bool IsSideways(float rotation)
    {
        float abs = MathF.Abs(rotation);
        float quarter = MathF.PI * 0.5f;
        return MathF.Abs(abs - quarter) < 0.35f || MathF.Abs(abs - 90f) < 12f;
    }

    private static void EmitDiagnostic(IPluginLog log, string text)
    {
        if (text == lastDiagnostic)
            return;
        lastDiagnostic = text;
        log.Debug($"[VisualPublicState] {text}");
    }

    private static IEnumerable<VisualTile> OrderRiver(IEnumerable<VisualTile> source, int seat)
    {
        static int Q(float v) => (int)MathF.Round(v / 18f);
        return seat switch
        {
            0 => source.OrderBy(v => Q(v.Y)).ThenBy(v => v.X),
            1 => source.OrderBy(v => Q(v.X)).ThenBy(v => v.Y),
            2 => source.OrderByDescending(v => Q(v.Y)).ThenByDescending(v => v.X),
            3 => source.OrderByDescending(v => Q(v.X)).ThenByDescending(v => v.Y),
            _ => source,
        };
    }

    private static List<VisualTile> ReadVisibleTiles(AtkUnitBase* unit)
    {
        var result = new List<VisualTile>(128);
        var seenImages = new HashSet<nint>();
        var seenNodes = new HashSet<nint>();
        WalkManager(&unit->UldManager, result, seenImages, seenNodes, depth: 0);
        return result;
    }

    private static void WalkManager(
        AtkUldManager* manager,
        List<VisualTile> result,
        HashSet<nint> seenImages,
        HashSet<nint> seenNodes,
        int depth)
    {
        if (manager == null || manager->NodeList == null || depth > 8)
            return;

        for (int i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || !node->NodeFlags.HasFlag(NodeFlags.Visible))
                continue;
            if (!seenNodes.Add((nint)node))
                continue;

            if (node->Type == NodeType.Image)
                AddImage((AtkImageNode*)node, node, result, seenImages);

            int type = (int)node->Type;
            if (type < 1000 || type > 1100)
                continue;

            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component == null)
                continue;
            WalkManager(&componentNode->Component->UldManager, result, seenImages, seenNodes, depth + 1);
        }
    }

    private static void AddImage(AtkImageNode* image, AtkResNode* positionNode, List<VisualTile> result, HashSet<nint> seen)
    {
        if (!TryDecodeImage(image, out var tile))
            return;
        nint key = (nint)image;
        if (!seen.Add(key))
            return;
        GetAbsolutePosition(positionNode, out float x, out float y);
        x += positionNode->Width * 0.5f;
        y += positionNode->Height * 0.5f;
        result.Add(new VisualTile(tile, x, y, positionNode->Rotation, positionNode->NodeId, Radius: 0f));
    }

    private static bool TryDecodeImage(AtkImageNode* image, out Tile tile)
    {
        tile = default;
        if (image == null || image->PartsList == null)
            return false;
        var parts = image->PartsList;
        if (parts->Parts == null || image->PartId >= parts->PartCount)
            return false;
        var part = &parts->Parts[image->PartId];
        if (part->UldAsset == null || part->UldAsset->AtkTexture.Resource == null)
            return false;
        uint iconId = part->UldAsset->AtkTexture.Resource->IconId;
        foreach (int tileBase in new[] { 76041, 76001 })
        {
            int id = (int)iconId - tileBase;
            if (id >= 0 && id < Tile.Count34)
            {
                tile = Tile.FromId(id);
                return true;
            }
        }
        return false;
    }

    private static void GetAbsolutePosition(AtkResNode* node, out float x, out float y)
    {
        x = 0; y = 0;
        float sx = 1f, sy = 1f;
        int guard = 0;
        for (var current = node; current != null && guard++ < 64; current = current->ParentNode)
        {
            x += current->X * sx;
            y += current->Y * sy;
            sx *= current->ScaleX;
            sy *= current->ScaleY;
        }
    }
}
