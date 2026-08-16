using Mahjong.Plugin.Dalamud.ExternalAi;

namespace Mahjong.Plugin.Dalamud.Tests;

public class AkochanResponseTests
{
    [Fact]
    public void Discard_array_is_reduced_to_its_first_action()
    {
        var action = ExternalMjaiProcess.ParseAkochanResponse(
            """[{"actor":0,"pai":"5m","tsumogiri":false,"type":"dahai"}]""");

        Assert.NotNull(action);
        Assert.Equal("dahai", action!["type"]!.GetValue<string>());
        Assert.Equal("5m", action["pai"]!.GetValue<string>());
    }

    [Fact]
    public void Reach_array_uses_the_following_discard_tile()
    {
        var action = ExternalMjaiProcess.ParseAkochanResponse(
            """[{"actor":0,"type":"reach"},{"actor":0,"pai":"3p","tsumogiri":false,"type":"dahai"}]""");

        Assert.NotNull(action);
        Assert.Equal("reach", action!["type"]!.GetValue<string>());
        Assert.Equal("3p", action["pai"]!.GetValue<string>());
    }

    [Fact]
    public void Chi_array_preserves_the_atomic_follow_up_discard()
    {
        var action = ExternalMjaiProcess.ParseAkochanResponse(
            """[{"actor":0,"consumed":["6p","8p"],"pai":"7p","target":3,"type":"chi"},{"actor":0,"pai":"1m","tsumogiri":false,"type":"dahai"}]""");

        Assert.NotNull(action);
        Assert.Equal("chi", action!["type"]!.GetValue<string>());
        Assert.Equal("1m", action["_post_call_pai"]!.GetValue<string>());
        Assert.False(action["_post_call_tsumogiri"]!.GetValue<bool>());
    }

    [Fact]
    public void Pon_array_ignores_a_follow_up_discard_from_another_actor()
    {
        var action = ExternalMjaiProcess.ParseAkochanResponse(
            """[{"actor":0,"consumed":["5m","5m"],"pai":"5m","target":1,"type":"pon"},{"actor":1,"pai":"9s","tsumogiri":false,"type":"dahai"}]""");

        Assert.NotNull(action);
        Assert.Null(action!["_post_call_pai"]);
    }
}
