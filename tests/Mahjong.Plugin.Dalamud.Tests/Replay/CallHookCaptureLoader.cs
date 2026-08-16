using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Mahjong.Core;

namespace Mahjong.Plugin.Dalamud.Tests.Replay;

/// <summary>
/// Loads the CALL_HOOK_CAPTURE_*.jsonl diagnostic stream written by the live
/// plugin into replayable <see cref="StateSnapshot"/> sequences. Every
/// "state_snapshot" line is a full System.Text.Json serialization of the
/// snapshot the decision pipeline saw at that poll, so replaying the sequence
/// through MjaiSessionTracker/ExternalMjaiProcess reproduces field failures
/// without launching the game.
/// </summary>
internal static class CallHookCaptureLoader
{
    public static IReadOnlyList<StateSnapshot> LoadSnapshots(string jsonlPath) =>
        LoadSnapshotLines(File.ReadLines(jsonlPath));

    public static IReadOnlyList<StateSnapshot> LoadSnapshotLines(IEnumerable<string> lines)
    {
        var snapshots = new List<StateSnapshot>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (JsonNode.Parse(line) is not JsonObject record)
                continue;
            if (!string.Equals(
                    record["category"]?.GetValue<string>(),
                    "state_snapshot",
                    StringComparison.Ordinal))
                continue;
            if (record["payload"] is not JsonObject payload)
                continue;

            snapshots.Add(ParseSnapshot(payload));
        }
        return snapshots;
    }

    private static StateSnapshot ParseSnapshot(JsonObject p) => new(
        Hand: ParseTiles(p["Hand"]),
        OurMelds: ParseMelds(p["OurMelds"]),
        OurSeat: GetInt(p, "OurSeat"),
        OurRiichi: GetBool(p, "OurRiichi"),
        OurIppatsu: GetBool(p, "OurIppatsu"),
        OurDoubleRiichi: GetBool(p, "OurDoubleRiichi"),
        RoundWind: GetInt(p, "RoundWind"),
        Honba: GetInt(p, "Honba"),
        RiichiSticks: GetInt(p, "RiichiSticks"),
        Scores: ParseInts(p["Scores"]),
        DoraIndicators: ParseTiles(p["DoraIndicators"]),
        UraDoraIndicators: ParseTiles(p["UraDoraIndicators"]),
        WallRemaining: GetInt(p, "WallRemaining"),
        TurnIndex: GetInt(p, "TurnIndex"),
        DealerSeat: GetInt(p, "DealerSeat"),
        Seats: ParseSeats(p["Seats"]),
        Legal: ParseLegal(p["Legal"]),
        SchemaVersion: GetInt(p, "SchemaVersion", StateSnapshot.CurrentSchemaVersion),
        SeatInfoKnown: GetBool(p, "SeatInfoKnown"),
        AkaDora: GetInt(p, "AkaDora"),
        AddonStateCode: GetInt(p, "AddonStateCode", -1));

    private static LegalActions ParseLegal(JsonNode? node)
    {
        if (node is not JsonObject legal)
            return LegalActions.None;

        return new LegalActions(
            (ActionFlags)GetInt(legal, "Flags"),
            ParseTiles(legal["DiscardableTiles"]),
            ParseCandidates(legal["PonCandidates"]),
            ParseCandidates(legal["ChiCandidates"]),
            ParseCandidates(legal["KanCandidates"]));
    }

    private static IReadOnlyList<SeatView> ParseSeats(JsonNode? node)
    {
        if (node is not JsonArray seats)
            return StateSnapshot.Empty.Seats;

        return seats
            .OfType<JsonObject>()
            .Select(seat => new SeatView(
                Discards: ParseTiles(seat["Discards"]),
                DiscardIsTedashi: ParseBools(seat["DiscardIsTedashi"]),
                Melds: ParseMelds(seat["Melds"]),
                Riichi: GetBool(seat, "Riichi"),
                RiichiDiscardIndex: GetInt(seat, "RiichiDiscardIndex", -1),
                Ippatsu: GetBool(seat, "Ippatsu"),
                IsTenpaiCalled: GetBool(seat, "IsTenpaiCalled"),
                DiscardCount: GetInt(seat, "DiscardCount")))
            .ToArray();
    }

    private static IReadOnlyList<MeldCandidate> ParseCandidates(JsonNode? node)
    {
        if (node is not JsonArray array)
            return [];

        return array
            .OfType<JsonObject>()
            .Select(candidate => new MeldCandidate(
                (MeldKind)GetInt(candidate, "Kind"),
                ParseTile(candidate["ClaimedTile"]) ?? new Tile(byte.MaxValue),
                ParseTiles(candidate["HandTiles"]).ToArray(),
                GetInt(candidate, "FromSeat", -1)))
            .ToArray();
    }

    private static IReadOnlyList<Meld> ParseMelds(JsonNode? node)
    {
        if (node is not JsonArray array)
            return [];

        return array
            .OfType<JsonObject>()
            .Select(meld => new Meld(
                (MeldKind)GetInt(meld, "Kind"),
                ParseTiles(meld["Tiles"]).ToArray(),
                ParseTile(meld["ClaimedTile"]),
                GetInt(meld, "ClaimedFromSeat", -1)))
            .ToArray();
    }

    private static IReadOnlyList<Tile> ParseTiles(JsonNode? node)
    {
        if (node is not JsonArray array)
            return [];

        var tiles = new List<Tile>(array.Count);
        foreach (JsonNode? entry in array)
        {
            if (ParseTile(entry) is { } tile)
                tiles.Add(tile);
        }
        return tiles;
    }

    private static Tile? ParseTile(JsonNode? node) => node switch
    {
        JsonObject obj when obj["Id"] is { } id => Tile.FromId(id.GetValue<int>()),
        JsonValue value when value.TryGetValue(out int id) => Tile.FromId(id),
        _ => null,
    };

    private static IReadOnlyList<int> ParseInts(JsonNode? node)
    {
        if (node is not JsonArray array)
            return [0, 0, 0, 0];
        return array.Select(entry => entry?.GetValue<int>() ?? 0).ToArray();
    }

    private static IReadOnlyList<bool> ParseBools(JsonNode? node)
    {
        if (node is not JsonArray array)
            return [];
        return array.Select(entry => entry?.GetValue<bool>() ?? false).ToArray();
    }

    private static int GetInt(JsonObject obj, string name, int fallback = 0) =>
        obj[name] is JsonValue value && value.TryGetValue(out int parsed) ? parsed : fallback;

    private static bool GetBool(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue(out bool parsed) && parsed;
}
