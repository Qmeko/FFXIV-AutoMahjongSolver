using Mahjong.Plugin.Dalamud.ExternalAi;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class AkochanRuntimeLocatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dms-ak-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(AkochanInferenceProfile.Realtime, "setup_mjai_realtime.json")]
    [InlineData(AkochanInferenceProfile.Precision, "setup_mjai.json")]
    public void Resolves_requested_search_profile(AkochanInferenceProfile profile, string expectedTactics)
    {
        string runtime = Path.Combine(root, "AkochanRuntime");
        Directory.CreateDirectory(Path.Combine(runtime, "params"));
        File.WriteAllText(Path.Combine(runtime, "akochan_pipe.exe"), string.Empty);
        File.WriteAllText(Path.Combine(runtime, "setup_mjai.json"), "{}");
        File.WriteAllText(Path.Combine(runtime, "setup_mjai_realtime.json"), "{}");

        bool ok = AkochanRuntimeLocator.TryResolve(
            root,
            new Configuration { AkochanInferenceProfile = profile },
            out MjaiLaunchSpec launch,
            out string error);

        Assert.True(ok, error);
        Assert.StartsWith(expectedTactics, launch.Arguments);
        Assert.Contains($"profile={profile}", launch.Identity);
        Assert.Equal("8", launch.Environment["OMP_NUM_THREADS"]);
        Assert.Equal("FALSE", launch.Environment["OMP_DYNAMIC"]);
        Assert.Equal("TRUE", launch.Environment["OMP_PROC_BIND"]);
        Assert.Equal("cores", launch.Environment["OMP_PLACES"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
