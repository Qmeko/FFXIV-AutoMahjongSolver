namespace Mahjong.Plugin.Dalamud.Composition;

internal sealed class ConfigMigratorV6ToV7 : IConfigMigrator<Configuration>
{
    public int FromVersion => 6;
    public int ToVersion => 7;

    public Configuration Migrate(Configuration input) => input with
    {
        Version = ToVersion,
        AutoCallEnabled = false,
        AutoPonEnabled = true,
        AutoChiEnabled = true,
        AutoAnKanEnabled = true,
        AutoMinKanEnabled = true,
        AutoShouMinKanEnabled = true,
    };
}
