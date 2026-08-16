using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mahjong.Plugin.Dalamud.ExternalAi;

internal static class MjaiJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    public static JsonObject Object(object value) =>
        JsonSerializer.SerializeToNode(value, Options)?.AsObject()
        ?? throw new InvalidOperationException("Could not serialize mjai event");

    public static string SerializeBatch(IEnumerable<JsonObject> events)
    {
        var array = new JsonArray();
        int index = 0;
        foreach (var evt in events)
        {
            ValidateEvent(evt, index++);
            array.Add(evt);
        }
        return array.ToJsonString(Options);
    }

    private static void ValidateEvent(JsonObject evt, int index)
    {
        string type = evt["type"]?.GetValue<string>() ?? throw new InvalidDataException($"mjai event[{index}] has no type");
        if (evt["actor"] is JsonValue actorNode
            && actorNode.TryGetValue<int>(out int actor)
            && actor is < 0 or > 3)
            throw new InvalidDataException($"mjai event[{index}] type={type} has invalid actor={actor}");

        if (evt["pai"] is JsonValue paiNode && paiNode.TryGetValue<string>(out string? pai))
        {
            bool unknownAllowed = type == "tsumo";
            if (!IsValidTileText(pai, unknownAllowed))
                throw new InvalidDataException($"mjai event[{index}] type={type} has invalid pai={pai}");
        }

        if (evt["dora_marker"] is JsonValue doraNode
            && doraNode.TryGetValue<string>(out string? dora)
            && !IsValidTileText(dora, unknownAllowed: true))
            throw new InvalidDataException($"mjai event[{index}] type={type} has invalid dora_marker={dora}");

        if (evt["consumed"] is JsonArray consumed)
        {
            foreach (JsonNode? node in consumed)
            {
                string? value = node?.GetValue<string>();
                if (!IsValidTileText(value, unknownAllowed: false))
                    throw new InvalidDataException($"mjai event[{index}] type={type} has invalid consumed tile={value}");
            }
        }
    }

    private static bool IsValidTileText(string? value, bool unknownAllowed)
    {
        if (value == "?")
            return unknownAllowed;
        return TryParseTile(value, out _);
    }

    public static JsonObject? ParseObject(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string EncodeTile(global::Mahjong.Core.Tile tile)
    {
        if (tile.Id >= global::Mahjong.Core.Tile.Count34)
            throw new InvalidDataException($"Tile id {tile.Id} is outside the 34-tile space");

        return tile.Suit switch
        {
        TileSuit.Man => $"{tile.Number}m",
        TileSuit.Pin => $"{tile.Number}p",
        TileSuit.Sou => $"{tile.Number}s",
        TileSuit.Honor => tile.HonorNumber switch
        {
            1 => "E",
            2 => "S",
            3 => "W",
            4 => "N",
            5 => "P",
            6 => "F",
            7 => "C",
            _ => "?",
        },
            _ => throw new InvalidDataException($"Unsupported tile suit for id {tile.Id}"),
        };
    }

    public static bool TryParseTile(string? value, out Tile tile)
    {
        tile = default;
        if (string.IsNullOrWhiteSpace(value) || value == "?")
            return false;

        value = value.Trim();
        if (value.Length == 1)
        {
            int honor = value.ToUpperInvariant() switch
            {
                "E" => 1,
                "S" => 2,
                "W" => 3,
                "N" => 4,
                "P" => 5,
                "F" => 6,
                "C" => 7,
                _ => 0,
            };
            if (honor == 0)
                return false;
            tile = global::Mahjong.Core.Tile.FromId(27 + honor - 1);
            return true;
        }

        // mjai uses 0m/0p/0s for red fives. Tile itself is 34-space, so normalize to 5.
        if (value.Length != 2 || value[1] is not ('m' or 'p' or 's'))
            return false;
        int number = value[0] - '0';
        if (number == 0)
            number = 5;
        if (number is < 1 or > 9)
            return false;
        int suitBase = value[1] switch { 'm' => 0, 'p' => 9, _ => 18 };
        tile = global::Mahjong.Core.Tile.FromId(suitBase + number - 1);
        return true;
    }
}
