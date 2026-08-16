using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Composition;

internal sealed class ConfigMigratorV3ToV4 : IConfigMigrator<Configuration>
{
    public int FromVersion => 3;
    public int ToVersion => 4;

    public Configuration Migrate(Configuration input) => input with
    {
        Version = ToVersion,
        AiProvider = input.AiProvider == AiProvider.ExternalMjai
            && !string.IsNullOrWhiteSpace(input.ExternalAiExecutable)
                ? AiProvider.ExternalMjai
                : AiProvider.BundledMortal,
        ExternalAiFallbackToBuiltIn = true,
        ExternalAiTimeoutMs = input.ExternalAiTimeoutMs <= 0 ? 5000 : input.ExternalAiTimeoutMs,
        ExternalAiStartupTimeoutMs = input.ExternalAiStartupTimeoutMs <= 0
            ? 120000
            : input.ExternalAiStartupTimeoutMs,
        MortalServer = string.IsNullOrWhiteSpace(input.MortalServer)
            ? "http://server.akagiot.org/"
            : input.MortalServer,
    };
}
