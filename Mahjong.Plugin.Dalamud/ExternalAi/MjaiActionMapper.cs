using System.Text.Json.Nodes;

namespace Mahjong.Plugin.Dalamud.ExternalAi;

internal static class MjaiActionMapper
{
    public static bool TryMap(
        JsonObject response,
        StateSnapshot state,
        ActionChoice builtIn,
        out ActionChoice choice,
        string source = "Mortal",
        bool allowUnreliableCallTarget = false)
    {
        choice = builtIn;
        string type = response["type"]?.GetValue<string>() ?? "none";
        int actor = response["actor"]?.GetValue<int>() ?? state.OurSeat;
        if (type is not "none" && actor != state.OurSeat)
            return false;

        switch (type)
        {
            case "none":
                choice = ActionChoice.Pass($"{source}: pass");
                return IsLegal(choice, state);
            case "dahai":
                if (!MjaiJson.TryParseTile(response["pai"]?.GetValue<string>(), out var discard))
                    return false;
                choice = ActionChoice.Discard(discard, $"{source}: discard");
                return IsLegal(choice, state);
            case "reach":
            {
                Tile tile;
                if (!MjaiJson.TryParseTile(response["pai"]?.GetValue<string>(), out tile))
                {
                    if (builtIn.DiscardTile is not { } fallbackTile)
                        return false;
                    tile = fallbackTile;
                }
                choice = ActionChoice.DeclareRiichi(tile, $"{source}: riichi");
                return IsLegal(choice, state);
            }
            case "hora":
            {
                int target = response["target"]?.GetValue<int>() ?? actor;
                choice = actor == target
                    ? ActionChoice.DeclareTsumo($"{source}: tsumo")
                    : ActionChoice.DeclareRon($"{source}: ron");
                return IsLegal(choice, state);
            }
            case "pon":
                return TryMapCall(MeldKind.Pon, ActionKind.Pon, response, state, source, allowUnreliableCallTarget, out choice);
            case "chi":
                return TryMapCall(MeldKind.Chi, ActionKind.Chi, response, state, source, allowUnreliableCallTarget, out choice);
            case "daiminkan":
                return TryMapCall(MeldKind.MinKan, ActionKind.MinKan, response, state, source, allowUnreliableCallTarget, out choice);
            case "ankan":
                return TryMapCall(MeldKind.AnKan, ActionKind.AnKan, response, state, source, allowUnreliableCallTarget, out choice);
            case "kakan":
                return TryMapCall(MeldKind.ShouMinKan, ActionKind.ShouMinKan, response, state, source, allowUnreliableCallTarget, out choice);
            default:
                return false;
        }
    }

    private static bool TryMapCall(
        MeldKind meldKind,
        ActionKind actionKind,
        JsonObject response,
        StateSnapshot state,
        string source,
        bool allowUnreliableCallTarget,
        out ActionChoice choice)
    {
        choice = ActionChoice.Pass();
        Tile? claimed = null;
        if (MjaiJson.TryParseTile(response["pai"]?.GetValue<string>(), out var parsed))
            claimed = parsed;

        ParsedConsumed consumed = ParseConsumed(response["consumed"] as JsonArray);
        int target = response["target"]?.GetValue<int>() ?? -1;

        IEnumerable<MeldCandidate> candidates = meldKind switch
        {
            MeldKind.Pon => state.Legal.PonCandidates,
            MeldKind.Chi => state.Legal.ChiCandidates,
            _ => state.Legal.KanCandidates,
        };

        var actionCandidates = candidates
            .Where(c => c.Kind == meldKind)
            .ToArray();

        var matching = actionCandidates
            .Where(c => !claimed.HasValue || c.ClaimedTile == claimed.Value)
            .Where(c => TargetMatchesCandidate(meldKind, state.OurSeat, target, c.FromSeat))
            .Where(c => meldKind == MeldKind.ShouMinKan || consumed.Tiles.Length == 0 || SameTileMultiset(c.HandTiles, consumed.Tiles))
            .ToArray();

        if (matching.Length == 0 && consumed.Tiles.Length > 0 && meldKind != MeldKind.ShouMinKan)
        {
            matching = actionCandidates
                .Where(c => TargetMatchesCandidate(meldKind, state.OurSeat, target, c.FromSeat))
                .Where(c => SameTileMultiset(c.HandTiles, consumed.Tiles))
                .ToArray();
        }

        bool normalizedUnreliableTarget = false;
        bool reconstructedFromExactResponse = false;
        if (matching.Length == 0 && allowUnreliableCallTarget
            && meldKind is MeldKind.Pon or MeldKind.Chi or MeldKind.MinKan)
        {
            // EMJ publishes the correct call kind/tiles but its reconstructed
            // candidate FromSeat is not authoritative. Akochan's target comes
            // from the ordered mjai discard event, so when the live prompt has
            // exactly one structurally matching candidate, ignore only the bad
            // seat field and preserve all tile/consumed checks.
            var relaxed = actionCandidates
                .Where(c => !claimed.HasValue || c.ClaimedTile == claimed.Value)
                .Where(c => consumed.Tiles.Length == 0 || SameTileMultiset(c.HandTiles, consumed.Tiles))
                .ToArray();

            if (relaxed.Length == 0 && consumed.Tiles.Length > 0)
            {
                relaxed = actionCandidates
                    .Where(c => SameTileMultiset(c.HandTiles, consumed.Tiles))
                    .ToArray();
            }

            var normalized = CollapseEquivalentCallCandidates(relaxed);
            if (normalized.Length == 1)
            {
                matching = normalized;
                normalizedUnreliableTarget = true;
            }
        }

        // The visible EMJ button can precede its candidate rows.  Akochan's
        // response already contains the exact claimed tile and consumed tiles,
        // so reconstruct that single structural candidate after validating it
        // against the live hand and legal flag.  This avoids waiting forever for
        // UI metadata that is not required to identify the selected call.
        if (matching.Length == 0
            && allowUnreliableCallTarget
            && TryBuildExactResponseCandidate(
                meldKind, claimed, consumed.Tiles, target, state, out MeldCandidate reconstructed))
        {
            matching = [reconstructed];
            reconstructedFromExactResponse = true;
        }

        if (matching.Length == 0)
            return false;

        MeldCandidate selected = matching[0];

        // Akochan's target is taken from the ordered mjai discard event and is
        // authoritative. EMJ candidate rows can expose the right tiles with a
        // stale/wrong FromSeat while the call list animates. Keeping that stale
        // seat after mapping makes the committed call get published once for the
        // wrong opponent and then again for the real opponent, corrupting
        // Akochan's hand state. Normalize the accepted candidate itself so every
        // downstream consumer (meld tracker, recovery and incremental mjai) uses
        // one identical actor/target identity.
        if (allowUnreliableCallTarget
            && meldKind is MeldKind.Pon or MeldKind.Chi or MeldKind.MinKan
            && target is >= 0 and < 4
            && target != Math.Clamp(state.OurSeat, 0, 3))
        {
            int authoritativeRelativeSeat =
                (target - Math.Clamp(state.OurSeat, 0, 3) + 4) & 3;
            if (meldKind != MeldKind.Chi || authoritativeRelativeSeat == 3)
            {
                normalizedUnreliableTarget |= selected.FromSeat != authoritativeRelativeSeat;
                selected = selected with { FromSeat = authoritativeRelativeSeat };
            }
        }

        Tile? postCallDiscard = null;
        if (meldKind is MeldKind.Chi or MeldKind.Pon
            && response["_post_call_pai"] is JsonValue postCallNode
            && postCallNode.TryGetValue<string>(out string? postCallText)
            && MjaiJson.TryParseTile(postCallText, out Tile parsedPostCall)
            && IsValidPostCallDiscard(state.Hand, selected.HandTiles, parsedPostCall))
        {
            postCallDiscard = parsedPostCall;
        }

        choice = new ActionChoice(
            actionKind,
            Call: selected,
            Reasoning: reconstructedFromExactResponse
                ? $"{source}: {actionKind} (AI応答から候補復元)"
                : normalizedUnreliableTarget
                    ? $"{source}: {actionKind} (EMJ座席補正)"
                    : $"{source}: {actionKind}")
        {
            CallConsumedRed = AlignRedFlags(selected.HandTiles, consumed),
            PostCallDiscardTile = postCallDiscard,
        };
        return IsLegal(choice, state);
    }



    internal static bool TryBuildExactResponseCandidate(
        MeldKind meldKind,
        Tile? claimed,
        IReadOnlyList<Tile> consumed,
        int target,
        StateSnapshot state,
        out MeldCandidate candidate)
    {
        candidate = default;
        if (claimed is not { } claim || claim.Id >= Tile.Count34)
            return false;

        ActionFlags required = meldKind switch
        {
            MeldKind.Pon => ActionFlags.Pon,
            MeldKind.Chi => ActionFlags.Chi,
            MeldKind.MinKan => ActionFlags.MinKan,
            MeldKind.AnKan => ActionFlags.AnKan,
            MeldKind.ShouMinKan => ActionFlags.ShouMinKan,
            _ => ActionFlags.None,
        };
        if (required == ActionFlags.None || !state.Legal.Can(required))
            return false;

        int expectedConsumed = meldKind switch
        {
            MeldKind.Pon or MeldKind.Chi => 2,
            MeldKind.MinKan => 3,
            MeldKind.AnKan => 4,
            MeldKind.ShouMinKan => 1,
            _ => -1,
        };
        if (consumed.Count != expectedConsumed || !HandContainsMultiset(state.Hand, consumed))
            return false;

        int relativeSeat = -1;
        if (meldKind is MeldKind.Pon or MeldKind.Chi or MeldKind.MinKan)
        {
            if (target < 0 || target > 3 || target == Math.Clamp(state.OurSeat, 0, 3))
                return false;
            relativeSeat = (target - Math.Clamp(state.OurSeat, 0, 3) + 4) & 3;
        }

        bool shapeValid = meldKind switch
        {
            MeldKind.Pon => consumed.All(tile => tile.Id == claim.Id),
            MeldKind.MinKan => consumed.All(tile => tile.Id == claim.Id),
            MeldKind.AnKan => consumed.All(tile => tile.Id == claim.Id),
            MeldKind.ShouMinKan => consumed[0].Id == claim.Id,
            MeldKind.Chi => relativeSeat == 3 && IsValidChiShape(claim, consumed),
            _ => false,
        };
        if (!shapeValid)
            return false;

        candidate = new MeldCandidate(meldKind, claim, consumed.ToArray(), relativeSeat);
        return true;
    }

    private static bool IsValidChiShape(Tile claimed, IReadOnlyList<Tile> consumed)
    {
        if (claimed.Suit == TileSuit.Honor
            || consumed.Any(tile => tile.Suit != claimed.Suit || tile.Suit == TileSuit.Honor))
            return false;

        int[] ids = consumed.Append(claimed).Select(tile => (int)tile.Id).OrderBy(id => id).ToArray();
        return ids.Length == 3 && ids[1] == ids[0] + 1 && ids[2] == ids[1] + 1;
    }

    private static bool HandContainsMultiset(IReadOnlyList<Tile> hand, IReadOnlyList<Tile> required)
    {
        var counts = new int[Tile.Count34];
        foreach (Tile tile in hand)
        {
            if (tile.Id < Tile.Count34)
                counts[tile.Id]++;
        }
        foreach (Tile tile in required)
        {
            if (tile.Id >= Tile.Count34 || --counts[tile.Id] < 0)
                return false;
        }
        return true;
    }

    internal static bool IsValidPostCallDiscard(
        IReadOnlyList<Tile> handBeforeCall,
        IReadOnlyList<Tile> consumed,
        Tile discard)
    {
        if (discard.Id >= Tile.Count34)
            return false;

        var counts = new int[Tile.Count34];
        foreach (Tile tile in handBeforeCall)
        {
            if (tile.Id < Tile.Count34)
                counts[tile.Id]++;
        }

        foreach (Tile tile in consumed)
        {
            if (tile.Id >= Tile.Count34 || --counts[tile.Id] < 0)
                return false;
        }

        return counts[discard.Id] > 0;
    }

    private static MeldCandidate[] CollapseEquivalentCallCandidates(IEnumerable<MeldCandidate> candidates)
    {
        return candidates
            .GroupBy(candidate =>
                $"{(int)candidate.Kind}:{candidate.ClaimedTile.Id}:" +
                string.Join(',', candidate.HandTiles.Select(tile => tile.Id).OrderBy(id => id)))
            .Select(group => group.First())
            .ToArray();
    }

    private static bool TargetMatchesCandidate(
        MeldKind meldKind,
        int ourSeat,
        int target,
        int candidateRelativeSeat)
    {
        if (target < 0 || meldKind is MeldKind.AnKan or MeldKind.ShouMinKan)
            return true;
        if (candidateRelativeSeat < 0)
            return false;
        int absoluteTarget = (Math.Clamp(ourSeat, 0, 3) + Math.Clamp(candidateRelativeSeat, 0, 3)) & 3;
        return absoluteTarget == target;
    }

    private readonly record struct ParsedConsumed(Tile[] Tiles, bool[] RedFlags);

    private static ParsedConsumed ParseConsumed(JsonArray? array)
    {
        if (array is null || array.Count == 0)
            return new ParsedConsumed([], []);

        var tiles = new List<Tile>(array.Count);
        var red = new List<bool>(array.Count);
        foreach (JsonNode? node in array)
        {
            if (node is null)
                return new ParsedConsumed([], []);
            string text = node.GetValue<string>();
            if (!MjaiJson.TryParseTile(text, out var tile))
                return new ParsedConsumed([], []);
            tiles.Add(tile);
            red.Add(text.Length == 2 && text[0] == '0' && text[1] is 'm' or 'p' or 's');
        }
        return new ParsedConsumed([.. tiles], [.. red]);
    }

    private static bool[] AlignRedFlags(IReadOnlyList<Tile> candidateTiles, ParsedConsumed consumed)
    {
        if (candidateTiles.Count == 0 || consumed.Tiles.Length != candidateTiles.Count)
            return [];

        var used = new bool[consumed.Tiles.Length];
        var result = new bool[candidateTiles.Count];
        for (int i = 0; i < candidateTiles.Count; i++)
        {
            int found = -1;
            for (int j = 0; j < consumed.Tiles.Length; j++)
            {
                if (!used[j] && consumed.Tiles[j].Id == candidateTiles[i].Id)
                {
                    found = j;
                    break;
                }
            }
            if (found < 0)
                return [];
            used[found] = true;
            result[i] = consumed.RedFlags[found];
        }
        return result;
    }

    private static bool SameTileMultiset(IReadOnlyList<Tile> left, IReadOnlyList<Tile> right)
    {
        if (left.Count != right.Count)
            return false;
        var a = left.Select(t => t.Id).OrderBy(id => id).ToArray();
        var b = right.Select(t => t.Id).OrderBy(id => id).ToArray();
        return a.SequenceEqual(b);
    }

    private static bool IsDiscardable(Tile tile, StateSnapshot state)
    {
        // The live EMJ reader currently exposes the Discard flag but often has
        // no per-tile restriction list. An empty list means "all tiles in the
        // closed hand are selectable", not "nothing is selectable".
        return state.Legal.DiscardableTiles.Count == 0
            ? state.Hand.Contains(tile)
            : state.Legal.DiscardableTiles.Contains(tile);
    }

    public static bool IsLegal(ActionChoice choice, StateSnapshot state)
    {
        return choice.Kind switch
        {
            // Pass is a real decision only when the addon explicitly exposes
            // it.  Treating every non-discard snapshot as pass-capable keeps
            // a prior/pass-sentinel choice alive across UI transitions.
            ActionKind.Pass => state.Legal.Can(ActionFlags.Pass),
            ActionKind.Discard => state.Legal.Can(ActionFlags.Discard)
                && choice.DiscardTile is { } t
                && IsDiscardable(t, state),
            ActionKind.Riichi => state.Legal.Can(ActionFlags.Riichi)
                && choice.DiscardTile is { } rt
                && IsDiscardable(rt, state),
            ActionKind.Tsumo => state.Legal.Can(ActionFlags.Tsumo),
            ActionKind.Ron => state.Legal.Can(ActionFlags.Ron),
            ActionKind.Pon => state.Legal.Can(ActionFlags.Pon) && choice.Call is { Kind: MeldKind.Pon },
            ActionKind.Chi => state.Legal.Can(ActionFlags.Chi) && choice.Call is { Kind: MeldKind.Chi },
            ActionKind.AnKan => state.Legal.Can(ActionFlags.AnKan) && choice.Call is { Kind: MeldKind.AnKan },
            ActionKind.MinKan => state.Legal.Can(ActionFlags.MinKan) && choice.Call is { Kind: MeldKind.MinKan },
            ActionKind.ShouMinKan => state.Legal.Can(ActionFlags.ShouMinKan) && choice.Call is { Kind: MeldKind.ShouMinKan },
            _ => false,
        };
    }
}
