using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Composition;

internal sealed class ConfigMigratorV2ToV3 : IConfigMigrator<Configuration>
{
    public int FromVersion => 2;
    public int ToVersion => 3;

    public Configuration Migrate(Configuration input) => input with
    {
        Version = ToVersion,
        AiProvider = AiProvider.BuiltIn,
        ExternalAiFallbackToBuiltIn = true,
        ExternalAiTimeoutMs = input.ExternalAiTimeoutMs <= 0 ? 1500 : input.ExternalAiTimeoutMs,
    };
}
