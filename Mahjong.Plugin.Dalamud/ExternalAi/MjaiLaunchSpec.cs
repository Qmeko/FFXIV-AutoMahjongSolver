using System.Text.Json;

namespace Mahjong.Plugin.Dalamud.ExternalAi;

internal sealed record MjaiLaunchSpec(
    string Executable,
    string Arguments,
    string WorkingDirectory,
    string Identity,
    bool IsBundledMortal,
    IReadOnlyDictionary<string, string> Environment);

internal static class AkochanRuntimeLocator
{
    private const string RuntimeFolderName = "AkochanRuntime";

    public static bool TryResolve(
        string pluginAssemblyDirectory,
        Configuration configuration,
        out MjaiLaunchSpec launch,
        out string error)
    {
        foreach (string root in CandidateRoots(pluginAssemblyDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string executable = Path.Combine(root, "akochan_pipe.exe");
            string tacticsFile = configuration.AkochanInferenceProfile == AkochanInferenceProfile.Precision
                ? "setup_mjai.json"
                : "setup_mjai_realtime.json";
            string tactics = Path.Combine(root, tacticsFile);
            string parameters = Path.Combine(root, "params");
            if (!File.Exists(executable)
                || !File.Exists(tactics)
                || !Directory.Exists(parameters))
                continue;

            launch = new MjaiLaunchSpec(
                Executable: executable,
                Arguments: $"{tacticsFile} {{PLAYER_ID}}",
                WorkingDirectory: root,
                Identity: $"akochan:{root}:profile={configuration.AkochanInferenceProfile}:tactics={tacticsFile}",
                IsBundledMortal: false,
                Environment: new Dictionary<string, string>
                {
                    // Akochan is compiled with NPROCS=8. Its Windows build
                    // requires the OpenMP worker count to match that value.
                    ["OMP_NUM_THREADS"] = "8",
                    ["OMP_DYNAMIC"] = "FALSE",
                    ["OMP_PROC_BIND"] = "TRUE",
                    ["OMP_PLACES"] = "cores",
                });
            error = string.Empty;
            return true;
        }

        launch = null!;
        error = "プラグイン付属のAkochanランタイムが見つかりません。";
        return false;
    }

    private static IEnumerable<string> CandidateRoots(string pluginAssemblyDirectory)
    {
        if (!string.IsNullOrWhiteSpace(pluginAssemblyDirectory))
            yield return Path.Combine(pluginAssemblyDirectory, RuntimeFolderName);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "DomanMahjongSolverDebug", RuntimeFolderName);
    }
}

internal static class MortalRuntimeLocator
{
    private const string RuntimeFolderName = "MortalRuntime";

    public static bool TryResolve(
        string pluginAssemblyDirectory,
        Configuration configuration,
        out MjaiLaunchSpec launch,
        out string error)
    {
        foreach (string root in CandidateRoots(pluginAssemblyDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string python = Path.Combine(root, "venv", "Scripts", "python.exe");
            string bot = Path.Combine(root, "bot", "bot.py");
            string model = Path.Combine(root, "bot", "mortal.pth");
            string libriichi = Path.Combine(root, "bot", "libriichi", "libriichi-3.12-x86_64-pc-windows-msvc.pyd");

            if (!File.Exists(python) || !File.Exists(bot) || !File.Exists(model) || !File.Exists(libriichi))
                continue;

            string configDirectory = Path.Combine(root, "config");
            Directory.CreateDirectory(configDirectory);
            string configPath = Path.Combine(configDirectory, "doman-mortal.json");
            var botConfig = new Dictionary<string, object?>
            {
                ["online"] = configuration.MortalOnline,
                ["server"] = string.IsNullOrWhiteSpace(configuration.MortalServer)
                    ? "http://server.akagiot.org/"
                    : configuration.MortalServer,
                ["api_key"] = configuration.MortalApiKey ?? string.Empty,
            };
            File.WriteAllText(configPath, JsonSerializer.Serialize(botConfig));

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Akagi-MjaiBot-Mortal expects a path to a JSON file, not inline JSON.
                ["AKAGI_BOT_CONFIG"] = configPath,
                ["PYTHONUTF8"] = "1",
                ["PYTHONUNBUFFERED"] = "1",
                ["OMP_NUM_THREADS"] = Math.Clamp(configuration.MortalCpuThreads, 1, Math.Max(1, Environment.ProcessorCount)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["MKL_NUM_THREADS"] = Math.Clamp(configuration.MortalCpuThreads, 1, Math.Max(1, Environment.ProcessorCount)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["TORCH_NUM_THREADS"] = Math.Clamp(configuration.MortalCpuThreads, 1, Math.Max(1, Environment.ProcessorCount)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            launch = new MjaiLaunchSpec(
                Executable: python,
                Arguments: $"-u {Quote(bot)}",
                WorkingDirectory: Path.GetDirectoryName(bot) ?? root,
                Identity: $"bundled-mortal-voidshine-298k:{root}:{configuration.MortalOnline}:{configuration.MortalServer}:{configuration.MortalApiKey}:threads={configuration.MortalCpuThreads}",
                IsBundledMortal: true,
                Environment: environment);
            error = string.Empty;
            return true;
        }

        launch = null!;
        error = "Mortal runtime was not found. Run BUILD_DEBUG_PLUGIN.bat to install and test it.";
        return false;
    }

    public static string PreferredRuntimeRoot()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "DomanMahjongSolverDebug", RuntimeFolderName);
    }

    private static IEnumerable<string> CandidateRoots(string pluginAssemblyDirectory)
    {
        if (!string.IsNullOrWhiteSpace(pluginAssemblyDirectory))
            yield return Path.Combine(pluginAssemblyDirectory, RuntimeFolderName);

        yield return PreferredRuntimeRoot();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            yield return Path.Combine(appData, "DomanMahjongSolverDebug", RuntimeFolderName);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
