using System.Reflection;
using System.Text.Json.Nodes;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Mahjong.Policy.Abstractions;
using Mahjong.Plugin.Dalamud.Tests.Stubs;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Field capture 2026-08-01 23:06:55: the player drew the fourth 7p and Mortal
/// answered "ankan" (99.95%) while EMJ still exposed only the Discard flag.
/// The answer was rejected instead of deferred; the AnKan flag appeared 0.2s
/// later with nothing left to map, and even manual resyncs looped on the same
/// rejection forever ("まだ指示の喪失がある"). Own-kan answers and in-hand
/// discard answers must be retained until the legal surface catches up.
/// </summary>
public class OwnKanDeferralRegressionTests
{
    private static readonly Tile SevenPin = Tile.FromId(15);

    private static Tile[] HandWithFourSevenPin()
    {
        Tile[] hand = Enumerable.Range(0, 14).Select(i => Tile.FromId(i % Tile.Count34)).ToArray();
        hand[10] = SevenPin;
        hand[11] = SevenPin;
        hand[12] = SevenPin;
        hand[13] = SevenPin;
        return hand;
    }

    [Fact]
    public void Own_ankan_answer_is_deferred_while_the_flag_is_still_missing()
    {
        Tile[] hand14 = HandWithFourSevenPin();
        var discardOnly = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand14,
            Legal = new LegalActions(ActionFlags.Discard, hand14, [], [], []),
        };
        JsonObject ankan = JsonNode.Parse(
            """{"type":"ankan","actor":0,"consumed":["7p","7p","7p","7p"]}""")!.AsObject();

        Assert.True(ExternalMjaiProcess.ShouldDeferTransientLegalSurface(
            ExternalEngineKind.Primary, expectsDecision: true, ankan, discardOnly));
    }

    [Fact]
    public void Own_kakan_answer_is_deferred_while_the_flag_is_still_missing()
    {
        Tile[] hand11 = Enumerable.Range(0, 11).Select(i => Tile.FromId(i % Tile.Count34)).ToArray();
        var discardOnly = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand11,
            Legal = new LegalActions(ActionFlags.Discard, hand11, [], [], []),
        };
        JsonObject kakan = JsonNode.Parse(
            """{"type":"kakan","actor":0,"pai":"7p","consumed":["7p","7p","7p"]}""")!.AsObject();

        Assert.True(ExternalMjaiProcess.ShouldDeferTransientLegalSurface(
            ExternalEngineKind.Primary, expectsDecision: true, kakan, discardOnly));
    }

    [Fact]
    public void In_hand_discard_answer_is_deferred_instead_of_rejected()
    {
        // 2026-08-01 23:05:03: "dahai 4p" was rejected on a Discard+Riichi
        // surface whose DiscardableTiles row transiently omitted the 4p, and
        // the whole turn passed without an instruction.
        Tile fourPin = Tile.FromId(12);
        Tile[] hand14 = Enumerable.Range(0, 14).Select(i => Tile.FromId(i % Tile.Count34)).ToArray();
        hand14[5] = fourPin;
        var restricted = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand14,
            Legal = new LegalActions(
                ActionFlags.Discard | ActionFlags.Riichi,
                [Tile.FromId(0), Tile.FromId(1)],
                [], [], []),
        };
        JsonObject dahai = JsonNode.Parse(
            """{"type":"dahai","actor":0,"pai":"4p","tsumogiri":false}""")!.AsObject();

        Assert.True(ExternalMjaiProcess.ShouldDeferTransientLegalSurface(
            ExternalEngineKind.Primary, expectsDecision: true, dahai, restricted));
    }

    [Fact]
    public void Discard_of_a_tile_missing_from_the_hand_is_not_deferred()
    {
        // The diverged-hand-model watchdog must keep owning this case.
        Tile[] hand14 = Enumerable.Range(0, 14).Select(i => Tile.FromId(i)).ToArray();
        var state = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand14,
            Legal = new LegalActions(ActionFlags.Discard, hand14, [], [], []),
        };
        JsonObject dahai = JsonNode.Parse(
            """{"type":"dahai","actor":0,"pai":"9s","tsumogiri":false}""")!.AsObject();

        Assert.False(ExternalMjaiProcess.ShouldDeferTransientLegalSurface(
            ExternalEngineKind.Primary, expectsDecision: true, dahai, state));
    }

    [Fact]
    public void Deferred_ankan_is_published_once_the_flag_appears()
    {
        Tile[] hand14 = HandWithFourSevenPin();
        var kanSurface = StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand14,
            Legal = new LegalActions(
                ActionFlags.Discard | ActionFlags.AnKan | ActionFlags.Pass,
                hand14,
                [], [],
                [new MeldCandidate(
                    MeldKind.AnKan,
                    SevenPin,
                    [SevenPin, SevenPin, SevenPin, SevenPin],
                    FromSeat: 0)]),
        };
        JsonObject ankan = JsonNode.Parse(
            """{"type":"ankan","actor":0,"consumed":["7p","7p","7p","7p"]}""")!.AsObject();

        using var process = new ExternalMjaiProcess(new StubPluginLog(), string.Empty);
        SetPrivateField(
            process,
            "deferredDecisionPositionFingerprint",
            ExternalMjaiProcess.PositionFingerprint(kanSurface));
        SetPrivateField(process, "deferredDecisionResponse", ankan);

        Assert.True(process.TryGetDeferredDecisionChoice(kanSurface, out ActionChoice choice));
        Assert.Equal(ActionKind.AnKan, choice.Kind);
        Assert.Equal(MeldKind.AnKan, choice.Call!.Value.Kind);
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }
}
