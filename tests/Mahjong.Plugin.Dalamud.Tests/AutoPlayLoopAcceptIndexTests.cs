using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests;

public class AutoPlayLoopAcceptIndexTests
{
    private static MeldCandidate MakePon(int tileId, int fromSeat = 1) =>
        new(MeldKind.Pon, Tile.FromId(tileId), [Tile.FromId(tileId), Tile.FromId(tileId)], fromSeat);

    private static MeldCandidate MakeChi(int claimedId, int low, int high, int fromSeat = 3)
    {
        var handTiles = new List<Tile>();
        for (int id = low; id <= high; id++)
            if (id != claimedId)
                handTiles.Add(Tile.FromId(id));
        return new MeldCandidate(MeldKind.Chi, Tile.FromId(claimedId), handTiles.ToArray(), fromSeat);
    }

    private static MeldCandidate MakeKan(int tileId, int fromSeat = 1) =>
        new(MeldKind.MinKan, Tile.FromId(tileId),
            [Tile.FromId(tileId), Tile.FromId(tileId), Tile.FromId(tileId)], fromSeat);

    [Fact]
    public void Pon_alone_returns_index_0()
    {
        var legal = new LegalActions(
            ActionFlags.Pon | ActionFlags.Pass,
            [], [MakePon(5)], [], []);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Pon, legal, MakePon(5));
        Assert.Equal(0, idx);
    }

    [Fact]
    public void Pon_and_chi_simultaneous_chi_picks_index_after_pon()
    {
        var legal = new LegalActions(
            ActionFlags.Pon | ActionFlags.Chi | ActionFlags.Pass,
            [], [MakePon(5)], [MakeChi(3, 2, 4)], []);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Chi, legal, MakeChi(3, 2, 4));
        Assert.Equal(1, idx);
    }

    [Fact]
    public void Pon_and_chi_simultaneous_pon_still_picks_index_0()
    {
        var legal = new LegalActions(
            ActionFlags.Pon | ActionFlags.Chi | ActionFlags.Pass,
            [], [MakePon(5)], [MakeChi(3, 2, 4)], []);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Pon, legal, MakePon(5));
        Assert.Equal(0, idx);
    }

    [Fact]
    public void Multi_chi_first_variant_picks_index_0()
    {
        var chi123 = MakeChi(claimedId: 1, low: 0, high: 2);
        var chi234 = MakeChi(claimedId: 1, low: 1, high: 3);
        var legal = new LegalActions(
            ActionFlags.Chi | ActionFlags.Pass,
            [], [], [chi123, chi234], []);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Chi, legal, chi123);
        Assert.Equal(0, idx);
    }

    [Fact]
    public void Multi_chi_accept_picks_single_chi_button_regardless_of_variant()
    {
        var chi123 = MakeChi(claimedId: 1, low: 0, high: 2);
        var chi234 = MakeChi(claimedId: 1, low: 1, high: 3);
        var legal = new LegalActions(
            ActionFlags.Chi | ActionFlags.Pass,
            [], [], [chi123, chi234], []);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Chi, legal, chi234);
        Assert.Equal(0, idx);
    }

    [Fact]
    public void Multi_chi_with_pon_chi_button_sits_after_pon_regardless_of_variant_count()
    {
        var chi0 = MakeChi(claimedId: 1, low: 0, high: 2);
        var chi1 = MakeChi(claimedId: 1, low: 1, high: 3);
        var chi2 = MakeChi(claimedId: 1, low: 2, high: 4);
        var legal = new LegalActions(
            ActionFlags.Pon | ActionFlags.Chi | ActionFlags.Pass,
            [], [MakePon(5)], [chi0, chi1, chi2], []);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Chi, legal, chi2);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void Multi_chi_pass_index_does_not_inflate_with_variant_count()
    {
        // Regression: chi=2 used to compute Pass=opt 2; addon has no slot 2 so the popup never closed.
        var chi123 = MakeChi(claimedId: 1, low: 0, high: 2);
        var chi234 = MakeChi(claimedId: 1, low: 1, high: 3);
        var legal = new LegalActions(
            ActionFlags.Chi | ActionFlags.Pass,
            [], [], [chi123, chi234], []);

        Assert.Equal(1, AutoPlayLoop.ComputePassIndex(legal));
    }

    [Fact]
    public void Pon_and_kan_simultaneous_kan_picks_index_1()
    {
        var legal = new LegalActions(
            ActionFlags.Pon | ActionFlags.MinKan | ActionFlags.Pass,
            [], [MakePon(5)], [], [MakeKan(5)]);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.MinKan, legal, MakeKan(5));
        Assert.Equal(1, idx);
    }

    [Fact]
    public void Ron_picks_first_index_when_only_action()
    {
        var legal = new LegalActions(
            ActionFlags.Ron | ActionFlags.Pass,
            [], [], [], []);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Ron, legal, null);
        Assert.Equal(0, idx);
    }

    [Fact]
    public void Riichi_with_chi_in_prompt_picks_index_after_chi_slots()
    {
        var chi = MakeChi(claimedId: 1, low: 0, high: 2);
        var legal = new LegalActions(
            ActionFlags.Chi | ActionFlags.Riichi | ActionFlags.Pass,
            [], [], [chi], []);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Riichi, legal, null);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void Tsumo_with_riichi_in_prompt_picks_last_accept_index()
    {
        var legal = new LegalActions(
            ActionFlags.Riichi | ActionFlags.Tsumo | ActionFlags.Pass,
            [], [], [], []);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Tsumo, legal, null);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void Chi_with_no_matching_candidate_falls_back_to_first_chi_slot()
    {
        var chi1 = MakeChi(claimedId: 1, low: 0, high: 2);
        var legal = new LegalActions(
            ActionFlags.Pon | ActionFlags.Chi | ActionFlags.Pass,
            [], [MakePon(5)], [chi1], []);

        var ghostChi = MakeChi(claimedId: 8, low: 7, high: 9);
        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Chi, legal, ghostChi);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void Ron_with_pon_offered_picks_index_after_pon()
    {
        var legal = new LegalActions(
            ActionFlags.Pon | ActionFlags.Ron | ActionFlags.Pass,
            [], [MakePon(5)], [], []);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Ron, legal, null);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void AnKan_alone_picks_index_0()
    {
        var legal = new LegalActions(
            ActionFlags.AnKan | ActionFlags.Discard,
            [], [], [], [MakeKan(5)]);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.AnKan, legal, null);
        Assert.Equal(0, idx);
    }

    [Fact]
    public void AnKan_with_riichi_and_tsumo_picks_first_kan_slot()
    {
        var legal = new LegalActions(
            ActionFlags.AnKan | ActionFlags.Riichi | ActionFlags.Tsumo | ActionFlags.Discard,
            [], [], [], [MakeKan(5)]);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.AnKan, legal, null);
        Assert.Equal(0, idx);
    }

    [Fact]
    public void Riichi_at_self_declare_with_ankan_picks_index_after_ankan()
    {
        var legal = new LegalActions(
            ActionFlags.AnKan | ActionFlags.Riichi | ActionFlags.Tsumo | ActionFlags.Discard,
            [], [], [], [MakeKan(5)]);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.Riichi, legal, null);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void ShouMinKan_with_ankan_offered_picks_index_after_ankan()
    {
        var legal = new LegalActions(
            ActionFlags.AnKan | ActionFlags.ShouMinKan | ActionFlags.Discard,
            [], [], [], [MakeKan(5)]);

        int idx = AutoPlayLoop.ComputeAcceptIndex(ActionKind.ShouMinKan, legal, null);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void Automatic_calls_master_switch_blocks_every_call_kind()
    {
        var cfg = new Configuration
        {
            AutoCallEnabled = false,
            AutoPonEnabled = true,
            AutoChiEnabled = true,
            AutoAnKanEnabled = true,
            AutoMinKanEnabled = true,
            AutoShouMinKanEnabled = true,
        };

        Assert.False(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.Pass));
        Assert.False(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.Pon));
        Assert.False(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.Chi));
        Assert.False(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.AnKan));
        Assert.False(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.MinKan));
        Assert.False(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.ShouMinKan));
    }

    [Fact]
    public void Automatic_call_subswitches_are_independent()
    {
        var cfg = new Configuration
        {
            AutoCallEnabled = true,
            AutoPassEnabled = false,
            AutoPonEnabled = false,
            AutoChiEnabled = true,
            AutoAnKanEnabled = false,
            AutoMinKanEnabled = true,
            AutoShouMinKanEnabled = false,
        };

        Assert.False(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.Pass));
        Assert.False(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.Pon));
        Assert.True(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.Chi));
        Assert.False(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.AnKan));
        Assert.True(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.MinKan));
        Assert.False(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.ShouMinKan));
        Assert.True(AutoPlayLoop.IsAutomaticCallAllowed(cfg, ActionKind.Ron));
    }

    [Fact]
    public void Call_only_automation_blocks_discard_and_riichi_prompt_actions()
    {
        var cfg = new Configuration
        {
            TosAccepted = true,
            AutomationArmed = true,
            SuggestionOnly = true,
            AutoCallEnabled = true,
            AutoPassEnabled = true,
        };

        Assert.True(AutoPlayLoop.IsCallOnlyAutomation(cfg));
    }

    [Fact]
    public void Disabled_ankan_in_hint_mode_falls_back_to_pass_when_pass_auto_enabled()
    {
        var cfg = new Configuration
        {
            TosAccepted = true,
            AutomationArmed = true,
            SuggestionOnly = true,
            AutoCallEnabled = true,
            AutoPassEnabled = true,
            AutoAnKanEnabled = false,
        };
        var snap = StateSnapshot.Empty with
        {
            Hand = Enumerable.Repeat(Tile.FromId(9), 14).ToArray(),
            Legal = new LegalActions(
                ActionFlags.Discard | ActionFlags.AnKan | ActionFlags.Pass,
                [],
                [], [], [MakeKan(9)]),
        };

        var choice = AutoPlayLoop.ResolveDisabledCallChoice(
            cfg, snap, ActionKind.AnKan, "auto-AnKan disabled by settings", null);

        Assert.Equal(ActionKind.Pass, choice.Kind);
    }

    [Fact]
    public void Disabled_ankan_in_full_autoplay_can_still_discard_fallback()
    {
        var cfg = new Configuration
        {
            TosAccepted = true,
            AutomationArmed = true,
            SuggestionOnly = false,
            AutoCallEnabled = true,
            AutoAnKanEnabled = false,
        };
        Tile discardTile = Tile.FromId(0);
        var snap = StateSnapshot.Empty with
        {
            Hand = new[] { discardTile }.Concat(Enumerable.Repeat(Tile.FromId(9), 13)).ToArray(),
            Legal = new LegalActions(
                ActionFlags.Discard | ActionFlags.AnKan | ActionFlags.Pass,
                [discardTile],
                [], [], [MakeKan(9)]),
        };

        var choice = AutoPlayLoop.ResolveDisabledCallChoice(
            cfg,
            snap,
            ActionKind.AnKan,
            "auto-AnKan disabled by settings",
            filtered => ActionChoice.Discard(discardTile, "built-in"));

        Assert.Equal(ActionKind.Discard, choice.Kind);
        Assert.Equal(discardTile, choice.DiscardTile);
    }

    [Fact]
    public void Prompt_dispatch_path_uses_opcode_11_for_pass_actions()
    {
        Assert.Equal("opcode-11(opt=2)", AutoPlayLoop.ResolvePromptDispatchPathForTest(2));
        Assert.Equal("(none)", AutoPlayLoop.ResolvePromptDispatchPathForTest(null));
    }
    [Fact]
    public void Multi_chi_pattern_selects_the_AI_consumed_sequence()
    {
        Tile claim = Tile.FromId(2); // 3m
        var choice = new ActionChoice(
            ActionKind.Chi,
            Call: new MeldCandidate(MeldKind.Chi, claim, [Tile.FromId(3), Tile.FromId(4)], 3));
        IReadOnlyList<(int Id, bool IsRed)[]> variants =
        [
            [(0, false), (1, false), (2, false)],
            [(2, false), (3, false), (4, false)],
        ];

        int index = AutoPlayLoop.FindCallPatternIndexForTest(variants, choice);

        Assert.Equal(1, index);
    }

    [Fact]
    public void Pon_pattern_selects_the_exact_red_five_consumption()
    {
        Tile fiveMan = Tile.FromId(4);
        var choice = new ActionChoice(
            ActionKind.Pon,
            Call: new MeldCandidate(MeldKind.Pon, fiveMan, [fiveMan, fiveMan], 1))
        {
            CallConsumedRed = [true, false],
        };
        IReadOnlyList<(int Id, bool IsRed)[]> variants =
        [
            [(4, false), (4, false), (4, false)],
            [(4, true), (4, false), (4, false)],
        ];

        int index = AutoPlayLoop.FindCallPatternIndexForTest(variants, choice);

        Assert.Equal(1, index);
    }

    [Fact]
    public void Mixed_discard_and_chi_flags_remain_a_self_turn()
    {
        Tile claim = Tile.FromId(8);
        var state = StateSnapshot.Empty with
        {
            Hand = Enumerable.Range(0, 14).Select(i => Tile.FromId(i % Tile.Count34)).ToArray(),
            Legal = new LegalActions(
                ActionFlags.Discard | ActionFlags.Chi | ActionFlags.Pass,
                [claim],
                [],
                [MakeChi(claimedId: 8, low: 6, high: 8)],
                []),
        };

        Assert.False(AutoPlayLoop.IsExternalCallPromptSurface(state));
    }

    [Fact]
    public void Thirteen_tile_chi_surface_is_an_external_call_prompt()
    {
        var state = StateSnapshot.Empty with
        {
            Hand = Enumerable.Range(0, 13).Select(i => Tile.FromId(i % Tile.Count34)).ToArray(),
            Legal = new LegalActions(
                ActionFlags.Chi | ActionFlags.Pass,
                [],
                [],
                [MakeChi(claimedId: 8, low: 6, high: 8)],
                []),
        };

        Assert.True(AutoPlayLoop.IsExternalCallPromptSurface(state));
    }

}
