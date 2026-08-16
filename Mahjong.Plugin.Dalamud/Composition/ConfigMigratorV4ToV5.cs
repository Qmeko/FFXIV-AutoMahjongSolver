using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Composition;

internal sealed class ConfigMigratorV4ToV5 : IConfigMigrator<Configuration>
{
    public int FromVersion => 4;
    public int ToVersion => 5;

    public Configuration Migrate(Configuration input) => input with
    {
        Version = ToVersion,
        HumanDelayEnabled = true,
        DiscardDelayMinMs = 3000,
        DiscardDelayMaxMs = 4000,
        TsumogiriDelayMinMs = 1200,
        TsumogiriDelayMaxMs = 2000,
        CallDelayMinMs = 2000,
        CallDelayMaxMs = 3500,
        RiichiDelayMinMs = 3000,
        RiichiDelayMaxMs = 4000,
        WinDelayMinMs = 500,
        WinDelayMaxMs = 1200,
        PassDelayMinMs = 1000,
        PassDelayMaxMs = 2000,
        TurnTimeBudgetMs = 15000,
        EmergencyImmediateThresholdMs = 3000,
        MortalCpuThreads = 4,
        MortalAutoRestart = true,
    };
}
