using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Policy;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.UI;

/// <summary>Locates hand tiles by geometry — walk visible nodes, cluster by Y, take the tightest horizontal row of expected length.</summary>
public sealed class HandOverlay : IDisposable
{
    private const float MinTileWidth = 28f;
    private const float MaxTileWidth = 120f;
    private const float MinTileHeight = 45f;
    private const float MaxTileHeight = 160f;

    private const float MaxRowYSpread = 12f;

    private readonly Plugin plugin;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly MahjongAddon addon;
    private bool disposed;

    /// <summary>Dev console toggle: outline every detected tile rect, not just the picked slot.</summary>
    public bool DebugDrawAllRects { get; set; }

    public HandOverlay(Plugin plugin, IDalamudPluginInterface pluginInterface, MahjongAddon addon)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(pluginInterface);
        ArgumentNullException.ThrowIfNull(addon);
        this.plugin = plugin;
        this.pluginInterface = pluginInterface;
        this.addon = addon;
        pluginInterface.UiBuilder.Draw += Draw;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        pluginInterface.UiBuilder.Draw -= Draw;
    }

    private unsafe void Draw()
    {
        var cfg = plugin.Configuration;
        bool prodEnabled = cfg.TosAccepted && cfg.ShowInGameHighlight && cfg.AutomationArmed && cfg.SuggestionOnly;
        if (!DebugDrawAllRects && !prodEnabled)
            return;

        if (!addon.TryGet(out var unit, out _))
            return;
        if (!unit->IsVisible)
            return;

        var viewportOffset = ImGui.GetMainViewport().Pos;
        uint meldContainer = plugin.AddonReader.ActiveLayout?.NodeIds.MeldContainer ?? 0u;
        var candidates = CollectTileCandidates(unit, meldContainer);

        if (DebugDrawAllRects)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var r = candidates[i];
                r.Pos += viewportOffset;
                DrawDebugOutline(r, i);
            }
        }

        if (!prodEnabled)
            return;

        var snap = plugin.Aggregator.Latest;
        if (snap is null || snap.Hand.Count < 2)
            return;

        var rects = PickHandRowFromCandidates(candidates, snap.Hand.Count);
        if (rects is null)
            return;

        var choice = plugin.Aggregator.LastChoice;
        if (choice is null)
            return;

        // A recommended call highlights the whole consumed set from the hand
        // (e.g. the 6m and 7m of a "chi 8m"), so the player sees exactly which
        // tiles the suggested meld uses while the call prompt is open.
        if (choice.Kind is ActionKind.Chi or ActionKind.Pon
                or ActionKind.MinKan or ActionKind.AnKan or ActionKind.ShouMinKan
            && choice.Call is { } call
            && call.HandTiles is { Length: > 0 })
        {
            var callSlots = plugin.AddonReader.FindRenderedHandIndexesOfTiles(call.HandTiles);
            var callRects = new List<(Vector2 Pos, Vector2 Size)>(callSlots.Count);
            foreach (int callSlot in callSlots)
            {
                if (callSlot < 0 || callSlot >= rects.Count)
                    continue;
                var r = rects[callSlot];
                r.Pos += viewportOffset;
                callRects.Add(r);
            }
            if (callRects.Count == 0)
                return;

            DrawCallSetHighlight(
                ImGui.GetForegroundDrawList(),
                callRects,
                cfg.HighlightColorCall.ToVector3(),
                Math.Clamp(cfg.HighlightIntensity, 0.4f, 1.6f),
                CallLabel(choice.Kind),
                cfg.HighlightStyle);
            return;
        }

        if (choice.DiscardTile is null)
            return;
        int slot = plugin.AddonReader.FindRenderedHandIndexOfTile(choice.DiscardTile.Value);
        if (slot < 0 || slot >= rects.Count)
            return;

        var rect = rects[slot];
        rect.Pos += viewportOffset;
        bool isDrawnTile = slot == snap.Hand.Count - 1;

        var color = (isDrawnTile ? cfg.HighlightColorTsumogiri : cfg.HighlightColorDiscard).ToVector3();
        float intensity = Math.Clamp(cfg.HighlightIntensity, 0.4f, 1.6f);
        var dl = ImGui.GetForegroundDrawList();

        switch (cfg.HighlightStyle)
        {
            case HighlightStyle.Arrow:
                DrawHighlightArrow(dl, rect, color, intensity, isDrawnTile);
                break;
            default:
                DrawHighlightNeonGlow(dl, rect, color, intensity);
                break;
        }
    }

    private static void DrawDebugOutline((Vector2 Pos, Vector2 Size) rect, int index)
    {
        var dl = ImGui.GetForegroundDrawList();
        var min = rect.Pos - new Vector2(1, 1);
        var max = rect.Pos + rect.Size + new Vector2(1, 1);
        dl.AddRect(min, max, Theme.Pack(Theme.Info, 0.8f), 2f, ImDrawFlags.None, 1.5f);
        dl.AddText(new Vector2(min.X + 2, min.Y + 1), Theme.Pack(Theme.Info), index.ToString());
    }

    /// <summary>Visible tile-sized nodes in NodeList order, excluding the meld subtree (<paramref name="meldContainerId"/>). Melded tiles render on the hand's row but are not part of the concealed hand, so leaving them in shifted the highlight after a call (#53). <paramref name="meldContainerId"/> 0 disables the exclusion.</summary>
    private static unsafe List<(Vector2 Pos, Vector2 Size)> CollectTileCandidates(AtkUnitBase* unit, uint meldContainerId)
    {
        // Parent-chain walk already includes root position and scale — do NOT add unit->X/Y or multiply by unit->Scale on top.
        var result = new List<(Vector2 Pos, Vector2 Size)>(32);
        var uld = unit->UldManager;
        if (uld.NodeList == null || uld.NodeListCount <= 0)
            return result;

        for (int i = 0; i < uld.NodeListCount; i++)
        {
            var n = uld.NodeList[i];
            if (n == null || !n->IsVisible())
                continue;
            if (meldContainerId != 0 && HasAncestorId(n, meldContainerId))
                continue;

            float w = n->Width;
            float h = n->Height;
            if (w < MinTileWidth || w > MaxTileWidth)
                continue;
            if (h < MinTileHeight || h > MaxTileHeight)
                continue;
            if (w > h)
                continue;

            AbsolutePosition(n, out float nx, out float ny, out float sx, out float sy);
            result.Add((new Vector2(nx, ny), new Vector2(w * sx, h * sy)));
        }

        return result;
    }

    /// <summary>The player's hand sits at the bottom of the screen, so take the bottom-most (highest-Y) tight row of <paramref name="expected"/> tiles, then sort left-to-right. Picking the *tightest* row instead let the perfectly-aligned wall slots along the top win once a call shrank the hand below the wall's tile count (#53).</summary>
    internal static List<(Vector2 Pos, Vector2 Size)>? PickHandRowFromCandidates(List<(Vector2 Pos, Vector2 Size)> candidates, int expected)
    {
        if (candidates.Count < expected || expected <= 0)
            return null;

        // One rendered tile can contribute frame, image and effect nodes with
        // almost identical bounds. Collapse those layers into one physical
        // slot before indexing the hand.
        var physical = DeduplicateOverlappingRects(candidates);
        if (physical.Count < expected)
            return null;
        physical.Sort((a, b) => a.Pos.Y.CompareTo(b.Pos.Y));

        // Ascending Y, so the last window within MaxRowYSpread is the lowest on screen — the player's hand.
        float bottomY = physical.Max(rect => rect.Pos.Y);
        var bottomRow = physical
            .Where(rect => bottomY - rect.Pos.Y <= MaxRowYSpread)
            .OrderBy(rect => rect.Pos.X)
            .ToList();
        var selectedGroup = SplitHorizontalGroups(bottomRow)
            .Where(group => group.Count >= expected)
            .OrderBy(group => group.Count - expected)
            .ThenByDescending(group => group.Average(rect => rect.Pos.Y))
            .FirstOrDefault();
        if (selectedGroup is not null)
            return selectedGroup.Take(expected).ToList();

        return null;

        /*
        int bestStart = -1;
        for (int i = 0; i + expected <= physical.Count; i++)
        {
            float span = physical[i + expected - 1].Pos.Y - physical[i].Pos.Y;
            if (span <= MaxRowYSpread)
                bestStart = i;
        }

        if (bestStart < 0)
            return null;

        var selected = new List<(Vector2 Pos, Vector2 Size)>(expected);
        for (int i = bestStart; i < bestStart + expected; i++)
            selected.Add(physical[i]);
        selected.Sort((a, b) => a.Pos.X.CompareTo(b.Pos.X));
        return selected;
    }

        */
    }

    private static List<List<(Vector2 Pos, Vector2 Size)>> SplitHorizontalGroups(
        IReadOnlyList<(Vector2 Pos, Vector2 Size)> sortedRow)
    {
        var groups = new List<List<(Vector2 Pos, Vector2 Size)>>();
        foreach (var rect in sortedRow)
        {
            if (groups.Count == 0)
            {
                groups.Add([rect]);
                continue;
            }

            var current = groups[^1];
            var previous = current[^1];
            float typicalWidth = (previous.Size.X + rect.Size.X) * 0.5f;
            if (rect.Pos.X - previous.Pos.X > typicalWidth * 1.5f)
                groups.Add([rect]);
            else
                current.Add(rect);
        }
        return groups;
    }

    internal static List<(Vector2 Pos, Vector2 Size)> DeduplicateOverlappingRects(
        IReadOnlyList<(Vector2 Pos, Vector2 Size)> candidates)
    {
        var result = new List<(Vector2 Pos, Vector2 Size)>(candidates.Count);
        foreach (var candidate in candidates.OrderByDescending(RectArea))
        {
            if (!result.Any(existing => StronglyOverlaps(existing, candidate)))
                result.Add(candidate);
        }
        return result;
    }

    private static float RectArea((Vector2 Pos, Vector2 Size) rect) =>
        rect.Size.X * rect.Size.Y;

    private static bool StronglyOverlaps(
        (Vector2 Pos, Vector2 Size) a,
        (Vector2 Pos, Vector2 Size) b)
    {
        float left = MathF.Max(a.Pos.X, b.Pos.X);
        float top = MathF.Max(a.Pos.Y, b.Pos.Y);
        float right = MathF.Min(a.Pos.X + a.Size.X, b.Pos.X + b.Size.X);
        float bottom = MathF.Min(a.Pos.Y + a.Size.Y, b.Pos.Y + b.Size.Y);
        float intersection = MathF.Max(0, right - left) * MathF.Max(0, bottom - top);
        float smallerArea = MathF.Min(RectArea(a), RectArea(b));
        return smallerArea > 0 && intersection / smallerArea >= 0.72f;
    }

    /// <summary>True when <paramref name="id"/> is <paramref name="node"/> or any ancestor. Used to drop the meld subtree from the tile pool.</summary>
    private static unsafe bool HasAncestorId(AtkResNode* node, uint id)
    {
        for (var cur = node; cur != null; cur = cur->ParentNode)
            if (cur->NodeId == id)
                return true;
        return false;
    }

    /// <summary>Walks parent chain — result is game-window-local (before multi-viewport desktop offset) and already includes root node position.</summary>
    private static unsafe void AbsolutePosition(AtkResNode* node, out float x, out float y, out float scaleX, out float scaleY)
    {
        x = 0;
        y = 0;
        scaleX = 1f;
        scaleY = 1f;
        var cur = node;
        while (cur != null)
        {
            x = cur->X + x * cur->ScaleX;
            y = cur->Y + y * cur->ScaleY;
            scaleX *= cur->ScaleX;
            scaleY *= cur->ScaleY;
            cur = cur->ParentNode;
        }
    }

    /// <summary>Sine-eased pulse with a higher floor than Theme.Pulse so the overlay never fades to faint.</summary>
    private static float OverlayPulse(float period = 1.4f, float lo = 0.78f, float hi = 1.0f)
    {
        float t = (float)((DateTime.UtcNow.TimeOfDay.TotalSeconds % period) / period);
        float s = 0.5f + 0.5f * MathF.Sin(t * MathF.PI * 2f);
        return lo + (hi - lo) * s;
    }

    private static float ArrowBounce(float period = 0.9f, float amplitude = 5f)
    {
        float t = (float)((DateTime.UtcNow.TimeOfDay.TotalSeconds % period) / period);
        return amplitude * (0.5f + 0.5f * MathF.Sin(t * MathF.PI * 2f));
    }

    private static uint Pack(Vector3 rgb, float alpha)
        => Theme.Pack(new Vector4(rgb.X, rgb.Y, rgb.Z, Math.Clamp(alpha, 0f, 1f)));

    internal static void DrawHighlightNeonGlow(ImDrawListPtr dl, (Vector2 Pos, Vector2 Size) rect, Vector3 color, float intensity)
    {
        float pulse = OverlayPulse() * intensity;

        var min = rect.Pos - new Vector2(2, 2);
        var max = rect.Pos + rect.Size + new Vector2(2, 2);

        // Multi-ring outer glow: 4 expanding rings with decreasing alpha.
        for (int i = 4; i >= 1; i--)
        {
            float expand = i * 2.5f;
            float alpha = pulse * (0.42f / i);
            dl.AddRect(
                min - new Vector2(expand, expand),
                max + new Vector2(expand, expand),
                Pack(color, alpha),
                6f + expand, ImDrawFlags.None, 2f);
        }

        // Subtle inner fill so the tile reads as "active".
        dl.AddRectFilled(min, max, Pack(color, pulse * 0.18f), 6f);

        // Solid bright inner border.
        dl.AddRect(min, max, Pack(color, pulse), 6f, ImDrawFlags.None, 2.5f);

        // L-shaped corner brackets that pulse inward with the beat.
        float bracketLen = 10f + 2f * pulse;
        float bracketOff = 4f;
        float thick = 2.5f;
        uint bracket = Pack(color, pulse + 0.05f);
        DrawCornerBracket(dl, new Vector2(min.X - bracketOff, min.Y - bracketOff), bracketLen, +1, +1, thick, bracket);
        DrawCornerBracket(dl, new Vector2(max.X + bracketOff, min.Y - bracketOff), bracketLen, -1, +1, thick, bracket);
        DrawCornerBracket(dl, new Vector2(min.X - bracketOff, max.Y + bracketOff), bracketLen, +1, -1, thick, bracket);
        DrawCornerBracket(dl, new Vector2(max.X + bracketOff, max.Y + bracketOff), bracketLen, -1, -1, thick, bracket);

        // Bouncing arrow above the tile.
        float cx = (min.X + max.X) * 0.5f;
        float bounce = ArrowBounce();
        float tipY = min.Y - 8f - bounce;
        float baseY = tipY - 18f;
        uint arrowFill = Pack(color, pulse);
        uint arrowShadow = Theme.Pack(Theme.TileShadow, 0.6f);
        dl.AddTriangleFilled(
            new Vector2(cx - 13f, baseY + 2f),
            new Vector2(cx + 13f, baseY + 2f),
            new Vector2(cx, tipY + 2f),
            arrowShadow);
        dl.AddTriangleFilled(
            new Vector2(cx - 13f, baseY),
            new Vector2(cx + 13f, baseY),
            new Vector2(cx, tipY),
            arrowFill);
    }

    private static void DrawCornerBracket(ImDrawListPtr dl, Vector2 origin, float length, int dirX, int dirY, float thickness, uint color)
    {
        var hEnd = new Vector2(origin.X + length * dirX, origin.Y);
        var vEnd = new Vector2(origin.X, origin.Y + length * dirY);
        dl.AddLine(origin, hEnd, color, thickness);
        dl.AddLine(origin, vEnd, color, thickness);
    }

    internal static string CallLabel(ActionKind kind) => kind switch
    {
        ActionKind.Chi => "チー",
        ActionKind.Pon => "ポン",
        ActionKind.MinKan => "カン",
        ActionKind.AnKan => "暗カン",
        ActionKind.ShouMinKan => "加カン",
        _ => "鳴き",
    };

    /// <summary>
    /// Highlights every hand tile of a recommended call as one visual group:
    /// per-tile treatment matching the configured style, plus a single bouncing
    /// arrow with a call-verb pill centered above the whole set.
    /// </summary>
    internal static void DrawCallSetHighlight(
        ImDrawListPtr dl,
        IReadOnlyList<(Vector2 Pos, Vector2 Size)> rects,
        Vector3 color,
        float intensity,
        string label,
        HighlightStyle style)
    {
        if (rects.Count == 0)
            return;

        float pulse = OverlayPulse() * intensity;

        var groupMin = new Vector2(float.MaxValue, float.MaxValue);
        var groupMax = new Vector2(float.MinValue, float.MinValue);
        foreach (var rect in rects)
        {
            var min = rect.Pos - new Vector2(2, 2);
            var max = rect.Pos + rect.Size + new Vector2(2, 2);
            groupMin = Vector2.Min(groupMin, min);
            groupMax = Vector2.Max(groupMax, max);

            if (style == HighlightStyle.Arrow)
            {
                dl.AddRect(min, max, Pack(color, pulse * 0.9f), 6f, ImDrawFlags.None, 2f);
            }
            else
            {
                for (int i = 3; i >= 1; i--)
                {
                    float expand = i * 2.5f;
                    dl.AddRect(
                        min - new Vector2(expand, expand),
                        max + new Vector2(expand, expand),
                        Pack(color, pulse * (0.42f / i)),
                        6f + expand, ImDrawFlags.None, 2f);
                }
                dl.AddRectFilled(min, max, Pack(color, pulse * 0.18f), 6f);
                dl.AddRect(min, max, Pack(color, pulse), 6f, ImDrawFlags.None, 2.5f);
            }
        }

        // Connecting underline so the set reads as one meld even with gaps.
        float underlineY = groupMax.Y + 5f;
        dl.AddLine(
            new Vector2(groupMin.X, underlineY),
            new Vector2(groupMax.X, underlineY),
            Pack(color, pulse), 3f);

        // One arrow + label pill centered above the whole set.
        var textSize = ImGui.CalcTextSize(label);
        float cx = (groupMin.X + groupMax.X) * 0.5f;
        float bounce = ArrowBounce(0.85f, 6f);
        float arrowHalfWidth = 18f;
        float arrowHeight = 22f;
        float tipY = groupMin.Y - 8f - bounce;
        float arrowTopY = tipY - arrowHeight;

        float pillPadX = 10f;
        float pillPadY = 4f;
        float pillW = textSize.X + pillPadX * 2f;
        float pillH = textSize.Y + pillPadY * 2f;
        var pillMin = new Vector2(cx - pillW * 0.5f, arrowTopY - pillH - 2f);
        var pillMax = pillMin + new Vector2(pillW, pillH);

        uint shadow = Theme.Pack(Theme.TileShadow, 0.7f);
        dl.AddRectFilled(pillMin + new Vector2(1, 2), pillMax + new Vector2(1, 2), shadow, pillH * 0.5f);
        dl.AddTriangleFilled(
            new Vector2(cx - arrowHalfWidth + 1, arrowTopY + 2),
            new Vector2(cx + arrowHalfWidth + 1, arrowTopY + 2),
            new Vector2(cx + 1, tipY + 2),
            shadow);

        dl.AddRectFilled(pillMin, pillMax, Pack(color, pulse), pillH * 0.5f);
        dl.AddText(pillMin + new Vector2(pillPadX, pillPadY), Theme.Pack(new Vector4(0.07f, 0.08f, 0.10f, 1f)), label);
        dl.AddTriangleFilled(
            new Vector2(cx - arrowHalfWidth, arrowTopY),
            new Vector2(cx + arrowHalfWidth, arrowTopY),
            new Vector2(cx, tipY),
            Pack(color, pulse));
    }

    internal static void DrawHighlightArrow(ImDrawListPtr dl, (Vector2 Pos, Vector2 Size) rect, Vector3 color, float intensity, bool isDrawnTile)
    {
        float pulse = OverlayPulse(1.1f, 0.82f, 1.0f) * intensity;

        var min = rect.Pos - new Vector2(2, 2);
        var max = rect.Pos + rect.Size + new Vector2(2, 2);

        // Minimal tile treatment: thin outline so the user still sees which tile, but the art isn't covered.
        dl.AddRect(min, max, Pack(color, pulse * 0.9f), 6f, ImDrawFlags.None, 2f);

        // Big bouncing arrow with a label pill above the tile.
        string label = isDrawnTile ? "ツモ切り" : "打牌";
        var textSize = ImGui.CalcTextSize(label);

        float cx = (min.X + max.X) * 0.5f;
        float bounce = ArrowBounce(0.85f, 6f);

        // Arrow geometry (large, filled triangle).
        float arrowHalfWidth = 18f;
        float arrowHeight = 22f;
        float tipY = min.Y - 8f - bounce;
        float arrowTopY = tipY - arrowHeight;

        // Label pill sits above the arrow.
        float pillPadX = 10f;
        float pillPadY = 4f;
        float pillW = textSize.X + pillPadX * 2f;
        float pillH = textSize.Y + pillPadY * 2f;
        var pillMin = new Vector2(cx - pillW * 0.5f, arrowTopY - pillH - 2f);
        var pillMax = pillMin + new Vector2(pillW, pillH);

        // Shadow drop.
        uint shadow = Theme.Pack(Theme.TileShadow, 0.7f);
        dl.AddRectFilled(pillMin + new Vector2(1, 2), pillMax + new Vector2(1, 2), shadow, pillH * 0.5f);
        dl.AddTriangleFilled(
            new Vector2(cx - arrowHalfWidth + 1, arrowTopY + 2),
            new Vector2(cx + arrowHalfWidth + 1, arrowTopY + 2),
            new Vector2(cx + 1, tipY + 2),
            shadow);

        // Filled pill.
        dl.AddRectFilled(pillMin, pillMax, Pack(color, pulse), pillH * 0.5f);
        dl.AddText(pillMin + new Vector2(pillPadX, pillPadY), Theme.Pack(new Vector4(0.07f, 0.08f, 0.10f, 1f)), label);

        // Filled arrow.
        dl.AddTriangleFilled(
            new Vector2(cx - arrowHalfWidth, arrowTopY),
            new Vector2(cx + arrowHalfWidth, arrowTopY),
            new Vector2(cx, tipY),
            Pack(color, pulse));
    }

}
