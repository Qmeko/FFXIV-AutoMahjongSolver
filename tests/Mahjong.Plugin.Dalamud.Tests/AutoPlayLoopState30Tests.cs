using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests;

public class AutoPlayLoopState30Tests
{
    [Fact]
    public void State30_without_a_pending_structural_discard_remains_actionable()
    {
        Assert.False(AutoPlayLoop.ShouldSuppressState30DiscardSurface(
            pendingStructuralDiscard: false));
    }

    [Fact]
    public void State30_is_suppressed_while_our_discard_awaits_structural_commit()
    {
        Assert.True(AutoPlayLoop.ShouldSuppressState30DiscardSurface(
            pendingStructuralDiscard: true));
    }

    [Theory]
    [InlineData(14, 3, 13, 3, true)]
    [InlineData(14, 3, 14, 4, true)]
    [InlineData(14, 3, 14, 3, false)]
    [InlineData(11, 8, 11, 8, false)]
    public void Discard_commit_requires_hand_shrink_or_own_river_growth(
        int handAtDispatch,
        int ownDiscardsAtDispatch,
        int currentHand,
        int currentOwnDiscards,
        bool expected)
    {
        Assert.Equal(expected, AutoPlayLoop.HasStructuralDiscardCommitEvidence(
            handAtDispatch,
            ownDiscardsAtDispatch,
            currentHand,
            currentOwnDiscards));
    }

    [Fact]
    public void Open_call_outcome_is_identified_as_structural_commit_pending()
    {
        Tile claim = Tile.FromId(30);
        var pon = new ActionChoice(
            ActionKind.Pon,
            Call: new MeldCandidate(MeldKind.Pon, claim, [claim, claim], FromSeat: 1));

        Assert.True(AutoPlayLoop.IsCommittedOpenCall(pon));
        Assert.False(AutoPlayLoop.IsCommittedOpenCall(ActionChoice.Discard(Tile.FromId(0))));
        Assert.False(AutoPlayLoop.IsCommittedOpenCall(ActionChoice.Pass()));
    }
}
