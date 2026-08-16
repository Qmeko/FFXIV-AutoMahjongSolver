using System;
using System.IO;

namespace Mahjong.Plugin.Dalamud.Logging;

internal static class SessionLogDirectory
{
    private const string Root = @"J:\FFXIV-AutoMahjongSolver\Log";

    public static string Create()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string path = Path.Combine(Root, stamp);
        int suffix = 1;
        while (Directory.Exists(path))
            path = Path.Combine(Root, $"{stamp}_{suffix++:00}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Version.txt"), "0.8.1.4" + Environment.NewLine);
        File.WriteAllText(Path.Combine(path, "Session.txt"),
            $"StartedLocal={DateTime.Now:O}{Environment.NewLine}" +
            $"StartedUtc={DateTime.UtcNow:O}{Environment.NewLine}" +
            $"ClientLanguage={Plugin.ClientState.ClientLanguage}{Environment.NewLine}" +
            "Scope=Dalamud public services + FFXIVClientStructs + plugin-owned state only" + Environment.NewLine);
        return path;
    }
}
