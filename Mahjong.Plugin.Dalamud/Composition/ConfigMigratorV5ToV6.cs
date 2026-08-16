using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Composition;

internal sealed class ConfigMigratorV5ToV6 : IConfigMigrator<Configuration>
{
    public int FromVersion => 5;
    public int ToVersion => 6;

    public Configuration Migrate(Configuration input) => input with
    {
        Version = ToVersion,
        DiagnosticDecisionLogging = true,
    };
}
