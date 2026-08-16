using System.Reflection;
using System.Text.Json.Nodes;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Policy.Abstractions;
using Mahjong.Plugin.Dalamud.Tests.Stubs;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Field capture 2026-08-01 22:21:44: Mortal answered Pass for a chi window,
/// the answer was consumed once and cleared, and the 12 later polls while the
/// window stayed open produced no instruction — the overlay went blank for
/// ~4 seconds ("コールの選択がでなかった"). The deferred answer must stay
/// latched and be re-served on every poll while the same offer window is open.
/// </summary>
public class DeferredCallLatchRegressionTests
{
    private static StateSnapshot BuildLiveChiPrompt(out string offerKey)
    {
        Tile nineSou = Tile.FromId(26); // 9s
        Tile sevenSou = Tile.FromId(24);
        Tile eightSou = Tile.FromId(25);
        var chi = new MeldCandidate(MeldKind.Chi, nineSou, [sevenSou, eightSou], FromSeat: 3);
        Tile[] hand13 = Enumerable.Range(0, 13).Select(i => Tile.FromId(i % Tile.Count34)).ToArray();
        hand13[0] = sevenSou;
        hand13[1] = eightSou;
        SeatView[] seats = StateSnapshot.Empty.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = [nineSou],
            DiscardIsTedashi = [true],
            DiscardCount = 1,
        };
        offerKey = "3|9s";
        return StateSnapshot.Empty with
        {
            OurSeat = 0,
            Hand = hand13,
            Seats = seats,
            DoraIndicators = [Tile.FromId(4)],
            Legal = new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [chi], []),
            AddonStateCode = 15,
        };
    }

    [Fact]
    public void Deferred_call_answer_is_reserved_on_every_poll_while_the_window_is_open()
    {
        StateSnapshot liveCall = BuildLiveChiPrompt(out string offerKey);
        JsonObject response = JsonNode.Parse("""{"type":"none"}""")!.AsObject();

        using var process = new ExternalMjaiProcess(new StubPluginLog(), string.Empty);
        SetPrivateField(process, "deferredCallOfferKey", offerKey);
        SetPrivateField(process, "deferredCallResponse", response);
        SetPrivateField(process, "latestOpponentDiscardKey", offerKey);
        SetPrivateField(process, "deferredCallOfferDiscardCount", 1);

        // Twelve ~300ms polls hit this path in the field capture; every one of
        // them must keep returning the same Pass instead of going blank.
        for (int poll = 0; poll < 12; poll++)
        {
            Assert.True(
                process.TryGetDeferredCallChoice(liveCall, out ActionChoice choice),
                $"poll {poll} lost the latched call answer");
            Assert.Equal(ActionKind.Pass, choice.Kind);
        }
    }

    [Fact]
    public void Latched_answer_from_a_closed_window_with_the_same_key_is_dropped()
    {
        StateSnapshot liveCall = BuildLiveChiPrompt(out string offerKey);

        // The same actor discarded the same tile kind again later: his river is
        // strictly longer than it was when the old answer was stored. The stale
        // answer must not be re-served for this physically different window.
        Tile nineSou = Tile.FromId(26);
        SeatView[] seats = liveCall.Seats.ToArray();
        seats[3] = seats[3] with
        {
            Discards = [Tile.FromId(0), nineSou],
            DiscardIsTedashi = [true, true],
            DiscardCount = 2,
        };
        StateSnapshot laterWindow = liveCall with { Seats = seats };

        JsonObject response = JsonNode.Parse("""{"type":"none"}""")!.AsObject();
        using var process = new ExternalMjaiProcess(new StubPluginLog(), string.Empty);
        SetPrivateField(process, "deferredCallOfferKey", offerKey);
        SetPrivateField(process, "deferredCallResponse", response);
        SetPrivateField(process, "latestOpponentDiscardKey", offerKey);
        SetPrivateField(process, "deferredCallOfferDiscardCount", 1);

        Assert.False(process.TryGetDeferredCallChoice(laterWindow, out _));
        // The latch is gone, so the next poll cannot resurrect it either.
        Assert.False(process.TryGetDeferredCallChoice(laterWindow, out _));
    }

    [Fact]
    public void Own_discard_surface_clears_the_latched_call_answer()
    {
        // The clear on our own discard surface (TryChooseCore) resets every
        // latch field; a later poll of the same closed window finds nothing.
        JsonObject response = JsonNode.Parse("""{"type":"none"}""")!.AsObject();
        using var process = new ExternalMjaiProcess(new StubPluginLog(), string.Empty);
        SetPrivateField(process, "deferredCallOfferKey", "3|9s");
        SetPrivateField(process, "deferredCallResponse", response);
        SetPrivateField(process, "deferredCallServedKey", "3|9s");
        SetPrivateField(process, "deferredCallOfferDiscardCount", 1);

        MethodInfo clear = typeof(ExternalMjaiProcess).GetMethod(
            "ClearDeferredCallLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        clear.Invoke(process, null);

        Assert.Null(GetPrivateField<string?>(process, "deferredCallOfferKey"));
        Assert.Null(GetPrivateField<JsonObject?>(process, "deferredCallResponse"));
        Assert.Null(GetPrivateField<string?>(process, "deferredCallServedKey"));
        Assert.Equal(-1, GetPrivateField<int>(process, "deferredCallOfferDiscardCount"));
    }

    private const int EmjTextureBase = 76041;

    [Fact]
    public void Rendered_indexes_map_duplicate_pon_tiles_to_distinct_slots()
    {
        // Hand with two 6z copies at raw slots 4 and 9 (texture id 76041+32).
        int[] raw = new int[14];
        for (int i = 0; i < 13; i++)
            raw[i] = EmjTextureBase + i; // ids 0..12
        raw[4] = EmjTextureBase + 32;
        raw[9] = EmjTextureBase + 32;

        List<int> rendered = HandArrayDecoder.FindRenderedIndexes(
            raw, EmjTextureBase, stackalloc int[] { 32, 32 });

        Assert.Equal([4, 9], rendered);
    }

    [Fact]
    public void Rendered_indexes_skip_tiles_missing_from_the_hand()
    {
        int[] raw = new int[14];
        for (int i = 0; i < 13; i++)
            raw[i] = EmjTextureBase + i; // ids 0..12

        List<int> rendered = HandArrayDecoder.FindRenderedIndexes(
            raw, EmjTextureBase, stackalloc int[] { 5, 33 });

        Assert.Equal([5], rendered);
    }

    [Fact]
    public void Rendered_indexes_compress_post_call_gaps_like_the_single_lookup()
    {
        // Post-pon layout: slots 10..12 empty, drawn tile parked at slot 13.
        int[] raw = new int[14];
        for (int i = 0; i < 10; i++)
            raw[i] = EmjTextureBase + i; // ids 0..9
        raw[13] = EmjTextureBase + 20;

        List<int> rendered = HandArrayDecoder.FindRenderedIndexes(
            raw, EmjTextureBase, stackalloc int[] { 20, 3 });

        Assert.Equal([3, 10], rendered);
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private static T? GetPrivateField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        return (T?)field.GetValue(target);
    }
}
