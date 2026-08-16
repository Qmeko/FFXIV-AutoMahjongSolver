using Mahjong.Plugin.Dalamud.Actions;

namespace Mahjong.Plugin.Dalamud.Tests;

public class InputDispatcherTests
{
    [Fact]
    public void Throws_when_addon_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => new InputDispatcher(null!));
    }

    [Theory]
    [InlineData(15, 11, true)]
    [InlineData(15, 8, true)]
    [InlineData(15, 13, false)]
    [InlineData(6, 14, true)]
    [InlineData(30, 14, true)]
    public void Stale_call_prompt_state_uses_the_discard_handshake_only_after_the_hand_has_shrunk(
        int stateCode,
        int handCount,
        bool expected)
    {
        Assert.Equal(expected, InputDispatcher.IsTileClickDiscardSurface(
            stateCode,
            handCount,
            selfDeclareListCode: 6,
            callPromptCode: 15,
            ourTurnDiscardCode: 30));
    }
}
