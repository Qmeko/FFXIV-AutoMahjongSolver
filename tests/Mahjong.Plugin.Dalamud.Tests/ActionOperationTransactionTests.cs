using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests;

public class ActionOperationTransactionTests
{
    private static ActionOperationSnapshot Snapshot(
        int state = 6,
        int hand = 14,
        int melds = 0,
        int ownDiscards = 3,
        int totalDiscards = 12,
        int wall = 40,
        ActionFlags legal = ActionFlags.Discard,
        bool ourRiichi = false,
        bool riichiSurface = false) =>
        new(state, hand, melds, ownDiscards, totalDiscards, wall, legal, ourRiichi, riichiSurface);

    [Fact]
    public void Discard_tracks_selection_commit_and_structural_confirmation_separately()
    {
        var tx = new ActionOperationTransaction(
            "discard",
            ActionOperationKind.Discard,
            Snapshot(),
            ActionChoice.Discard(Tile.FromId(5)),
            Tile.FromId(5),
            expectedSelectionOpcode: 15,
            expectedSelectionArgument: null,
            expectedCommitOpcode: 7,
            expectedCommitArgument: 4);

        tx.MarkSelectionSent();
        Assert.Equal(ActionOperationPhase.SelectionSent, tx.Phase);

        Assert.True(tx.ObserveAgentEvent(15, 76046));
        Assert.Equal(ActionOperationPhase.SelectionObserved, tx.Phase);

        tx.MarkCommitSent();
        Assert.Equal(ActionOperationPhase.CommitSent, tx.Phase);

        Assert.True(tx.ObserveAgentEvent(7, 4));
        Assert.Equal(ActionOperationPhase.CommitObserved, tx.Phase);
        Assert.False(tx.IsTerminal);

        Assert.True(tx.ObserveSnapshot(Snapshot(hand: 13)));
        Assert.True(tx.StructurallyConfirmed);
        Assert.Equal(ActionOperationPhase.StructurallyConfirmed, tx.Phase);
    }

    [Fact]
    public void State30_without_hand_or_river_change_does_not_confirm_discard()
    {
        var tx = new ActionOperationTransaction(
            "discard",
            ActionOperationKind.Discard,
            Snapshot(),
            null,
            null,
            15,
            null,
            7,
            7);
        tx.MarkSelectionSent();
        tx.MarkCommitSent();
        tx.ObserveAgentEvent(15, 76059);
        tx.ObserveAgentEvent(7, 7);

        Assert.False(tx.ObserveSnapshot(Snapshot(state: 30)));
        Assert.False(tx.IsTerminal);
    }

    [Fact]
    public void River_growth_confirms_discard_even_when_hand_snapshot_lags()
    {
        var tx = new ActionOperationTransaction(
            "discard",
            ActionOperationKind.Discard,
            Snapshot(),
            null,
            null,
            15,
            null,
            7,
            7);

        Assert.True(tx.ObserveSnapshot(Snapshot(state: 30, ownDiscards: 4, totalDiscards: 13)));
        Assert.True(tx.StructurallyConfirmed);
    }

    [Fact]
    public void Riichi_declaration_confirms_only_on_candidate_surface_or_public_riichi()
    {
        var tx = new ActionOperationTransaction(
            "riichi-declaration",
            ActionOperationKind.RiichiDeclaration,
            Snapshot(legal: ActionFlags.Discard | ActionFlags.Riichi | ActionFlags.Pass),
            null,
            Tile.FromId(4),
            null,
            null,
            11,
            0);
        tx.MarkSelectionSent();
        tx.MarkCommitSent();
        tx.ObserveAgentEvent(11, 0);

        Assert.False(tx.ObserveSnapshot(Snapshot(
            state: 6,
            legal: ActionFlags.Discard | ActionFlags.Riichi | ActionFlags.Pass)));
        Assert.True(tx.ObserveSnapshot(Snapshot(
            state: 6,
            legal: ActionFlags.Discard | ActionFlags.Riichi | ActionFlags.Pass,
            riichiSurface: true)));
        Assert.True(tx.StructurallyConfirmed);
    }

    [Fact]
    public void Multi_chi_selection_waits_for_variant_commit_and_hand_shrink()
    {
        var tx = new ActionOperationTransaction(
            "chi",
            ActionOperationKind.OpenCall,
            Snapshot(state: 15, hand: 13, legal: ActionFlags.Chi | ActionFlags.Pass),
            null,
            Tile.FromId(12),
            11,
            0,
            12,
            null);
        tx.MarkSelectionSent();
        tx.ObserveAgentEvent(11, 0);

        Assert.Equal(ActionOperationPhase.SelectionObserved, tx.Phase);
        Assert.False(tx.ObserveSnapshot(Snapshot(state: 25, hand: 13, legal: ActionFlags.None)));

        tx.SetExpectedCommit(12, 1);
        tx.MarkCommitSent();
        tx.ObserveAgentEvent(12, 1);
        Assert.Equal(ActionOperationPhase.CommitObserved, tx.Phase);

        Assert.True(tx.ObserveSnapshot(Snapshot(state: 15, hand: 11, melds: 1, legal: ActionFlags.Discard)));
        Assert.True(tx.StructurallyConfirmed);
    }


    [Fact]
    public void Prompt_action_ignores_transient_state_change_until_commit_is_observed()
    {
        var tx = new ActionOperationTransaction(
            "pass",
            ActionOperationKind.PromptAction,
            Snapshot(state: 15, hand: 13, legal: ActionFlags.Pon | ActionFlags.Pass),
            ActionChoice.Pass(),
            null,
            11,
            1,
            11,
            1);
        tx.MarkSelectionSent();
        tx.MarkCommitSent();

        Assert.False(tx.ObserveSnapshot(Snapshot(state: 19, hand: 13, legal: ActionFlags.None)));
        Assert.False(tx.IsTerminal);

        Assert.True(tx.ObserveAgentEvent(11, 1));
        Assert.True(tx.ObserveSnapshot(Snapshot(state: 19, hand: 13, legal: ActionFlags.None)));
        Assert.True(tx.StructurallyConfirmed);
    }

    [Fact]
    public void New_hand_cancels_unfinished_transaction_without_timeout()
    {
        var tx = new ActionOperationTransaction(
            "discard",
            ActionOperationKind.Discard,
            Snapshot(wall: 18),
            null,
            null,
            15,
            null,
            7,
            3);

        Assert.True(tx.ObserveSnapshot(Snapshot(wall: 70)));
        Assert.True(tx.Cancelled);
        Assert.Equal(ActionOperationPhase.Cancelled, tx.Phase);
    }
}
