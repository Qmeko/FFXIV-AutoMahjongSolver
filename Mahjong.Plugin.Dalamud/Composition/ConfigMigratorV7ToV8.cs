namespace Mahjong.Plugin.Dalamud.Composition;

internal sealed class ConfigMigratorV7ToV8 : IConfigMigrator<Configuration>
{
    public int FromVersion => 7;
    public int ToVersion => 8;

    public Configuration Migrate(Configuration input) => input with
    {
        Version = ToVersion,
        AutoPassEnabled = true,
        PonDelayMinMs = input.CallDelayMinMs,
        PonDelayMaxMs = input.CallDelayMaxMs,
        ChiDelayMinMs = input.CallDelayMinMs,
        ChiDelayMaxMs = input.CallDelayMaxMs,
        MinKanDelayMinMs = input.CallDelayMinMs,
        MinKanDelayMaxMs = input.CallDelayMaxMs,
        AnKanDelayMinMs = input.CallDelayMinMs,
        AnKanDelayMaxMs = input.CallDelayMaxMs,
        ShouMinKanDelayMinMs = input.CallDelayMinMs,
        ShouMinKanDelayMaxMs = input.CallDelayMaxMs,
    };
}
