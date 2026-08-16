namespace Mahjong.Plugin.Dalamud.Composition;

internal sealed class ConfigMigratorV8ToV9 : IConfigMigrator<Configuration>
{
    public int FromVersion => 8;
    public int ToVersion => 9;

    public Configuration Migrate(Configuration input) => input with
    {
        Version = ToVersion,
        AkochanInferenceProfile = AkochanInferenceProfile.Realtime,
    };
}
