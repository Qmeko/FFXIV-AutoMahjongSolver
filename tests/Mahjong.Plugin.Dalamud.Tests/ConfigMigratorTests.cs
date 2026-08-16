using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Tests;

public class ConfigMigratorTests
{
    [Fact]
    public void V0ToV1_just_bumps_version()
    {
        var input = new Configuration { Version = 0, TosAccepted = true };
        var migrator = new ConfigMigratorV0ToV1();
        var output = migrator.Migrate(input);

        Assert.Equal(1, output.Version);
        Assert.True(output.TosAccepted);
    }

    [Fact]
    public void V0ToV1_returns_a_new_instance()
    {
        var input = new Configuration { Version = 0 };
        var migrator = new ConfigMigratorV0ToV1();
        var output = migrator.Migrate(input);
        Assert.NotSame(input, output);
    }

    [Fact]
    public void V0ToV1_declares_correct_versions()
    {
        var migrator = new ConfigMigratorV0ToV1();
        Assert.Equal(0, migrator.FromVersion);
        Assert.Equal(1, migrator.ToVersion);
    }

    [Fact]
    public void V1ToV2_mints_a_fresh_install_id_when_missing()
    {
        var input = new Configuration { Version = 1, InstallId = Guid.Empty };
        var migrator = new ConfigMigratorV1ToV2();
        var output = migrator.Migrate(input);

        Assert.Equal(2, output.Version);
        Assert.NotEqual(Guid.Empty, output.InstallId);
    }

    [Fact]
    public void V1ToV2_preserves_an_existing_install_id()
    {
        var existing = Guid.NewGuid();
        var input = new Configuration { Version = 1, InstallId = existing };
        var migrator = new ConfigMigratorV1ToV2();
        var output = migrator.Migrate(input);

        Assert.Equal(existing, output.InstallId);
    }

    [Fact]
    public void V1ToV2_declares_correct_versions()
    {
        var migrator = new ConfigMigratorV1ToV2();
        Assert.Equal(1, migrator.FromVersion);
        Assert.Equal(2, migrator.ToVersion);
    }

    [Fact]
    public void Full_chain_v0_to_v4_mints_install_id_and_preserves_other_fields()
    {
        var input = new Configuration
        {
            Version = 0,
            TosAccepted = true,
            HumanizedDelayMs = 800,
        };

        var migrators = new IConfigMigrator<Configuration>[]
        {
            new ConfigMigratorV0ToV1(),
            new ConfigMigratorV1ToV2(),
            new ConfigMigratorV2ToV3(),
            new ConfigMigratorV3ToV4(),
        };

        var output = ConfigMigrationRunner.Run(
            input, currentVersion: 0, targetVersion: 4, migrators);

        Assert.Equal(4, output.Version);
        Assert.True(output.TosAccepted);
        Assert.Equal(800, output.HumanizedDelayMs);
        Assert.NotEqual(Guid.Empty, output.InstallId);
    }

    [Fact]
    public void Chain_skips_already_completed_steps_when_starting_at_v1()
    {
        var input = new Configuration { Version = 1, InstallId = Guid.Empty };

        var migrators = new IConfigMigrator<Configuration>[]
        {
            new ConfigMigratorV0ToV1(),
            new ConfigMigratorV1ToV2(),
            new ConfigMigratorV2ToV3(),
            new ConfigMigratorV3ToV4(),
        };

        var output = ConfigMigrationRunner.Run(
            input, currentVersion: 1, targetVersion: 4, migrators);

        Assert.Equal(4, output.Version);
        Assert.NotEqual(Guid.Empty, output.InstallId);
    }

    [Fact]
    public void V2ToV3_adds_external_ai_defaults()
    {
        var output = new ConfigMigratorV2ToV3().Migrate(new Configuration { Version = 2 });
        Assert.Equal(3, output.Version);
        Assert.Equal(AiProvider.BuiltIn, output.AiProvider);
        Assert.True(output.ExternalAiFallbackToBuiltIn);
        // The record default is now 5000 ms; the migrator only replaces
        // non-positive legacy values, so a fresh V2 config keeps the default.
        Assert.Equal(5000, output.ExternalAiTimeoutMs);
    }

    [Fact]
    public void V3ToV4_selects_bundled_mortal_and_adds_runtime_defaults()
    {
        var output = new ConfigMigratorV3ToV4().Migrate(new Configuration
        {
            Version = 3,
            AiProvider = AiProvider.BuiltIn,
            ExternalAiTimeoutMs = 1500,
        });

        Assert.Equal(4, output.Version);
        Assert.Equal(AiProvider.BundledMortal, output.AiProvider);
        Assert.Equal(1500, output.ExternalAiTimeoutMs);
        Assert.Equal(120000, output.ExternalAiStartupTimeoutMs);
        Assert.True(output.ExternalAiFallbackToBuiltIn);
    }

    [Fact]
    public void V3ToV4_preserves_a_configured_manual_external_process()
    {
        var output = new ConfigMigratorV3ToV4().Migrate(new Configuration
        {
            Version = 3,
            AiProvider = AiProvider.ExternalMjai,
            ExternalAiExecutable = @"C:\bot\bot.exe",
        });

        Assert.Equal(AiProvider.ExternalMjai, output.AiProvider);
        Assert.Equal(@"C:\bot\bot.exe", output.ExternalAiExecutable);
    }

    [Fact]
    public void V8ToV9_enables_realtime_akochan_profile()
    {
        var output = new ConfigMigratorV8ToV9().Migrate(new Configuration
        {
            Version = 8,
            AkochanInferenceProfile = AkochanInferenceProfile.Precision,
        });

        Assert.Equal(9, output.Version);
        Assert.Equal(AkochanInferenceProfile.Realtime, output.AkochanInferenceProfile);
    }
}
