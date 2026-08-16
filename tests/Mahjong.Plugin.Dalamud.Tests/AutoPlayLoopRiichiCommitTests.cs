using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;

namespace Mahjong.Plugin.Dalamud.Tests;

public class AutoPlayLoopRiichiCommitTests
{
    [Fact]
    public void Initial_state6_riichi_list_requires_one_following_tick_commit()
    {
        Assert.True(AutoPlayLoop.ShouldCommitRiichiListSelection(
            state: 6,
            handCount: 14,
            chosenTilePresent: true,
            legal: ActionFlags.Discard | ActionFlags.Riichi | ActionFlags.Pass,
            candidateSurfaceReady: false));
    }

    [Fact]
    public void Candidate_surface_does_not_commit_riichi_again()
    {
        Assert.False(AutoPlayLoop.ShouldCommitRiichiListSelection(
            state: 6,
            handCount: 14,
            chosenTilePresent: true,
            legal: ActionFlags.Discard | ActionFlags.Riichi | ActionFlags.Pass,
            candidateSurfaceReady: true));
    }

    [Theory]
    [InlineData(15, 14, true)]
    [InlineData(6, 13, true)]
    [InlineData(6, 14, false)]
    public void Changed_surface_cancels_the_queued_commit(
        int state,
        int handCount,
        bool chosenTilePresent)
    {
        Assert.False(AutoPlayLoop.ShouldCommitRiichiListSelection(
            state,
            handCount,
            chosenTilePresent,
            ActionFlags.Discard | ActionFlags.Riichi | ActionFlags.Pass,
            candidateSurfaceReady: false));
    }
}
