using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Mahjong.Plugin.Dalamud.UI.DebugTabs;

/// <summary>Event logger, hook health, telemetry status, errors/findings tail, overlay debug.</summary>
internal sealed class DiagnosticsTab
{
    private const int TailLines = 5;

    private readonly DevConsoleContext ctx;

    public DiagnosticsTab(DevConsoleContext ctx) => this.ctx = ctx;

    public void Draw()
    {
        DrawEventLoggerCard();
        ImGui.Dummy(new Vector2(0, 4));
        DrawDiscardCaptureCard();
        ImGui.Dummy(new Vector2(0, 4));
        DrawTelemetryCard();
        ImGui.Dummy(new Vector2(0, 4));
        DrawStreamsCard();
        ImGui.Dummy(new Vector2(0, 4));
        DrawHandOverlayDebugCard();
    }

    private void DrawEventLoggerCard()
    {
        using (Theme.BeginCard("diag-log"))
        {
            Theme.SectionHeader("イベントロガー");
            Theme.Subtle("麻雀アドオンが受信したすべてのUIコールバックを記録します。ボタン番号の解析に使用します。負荷が高いため、作業後は無効にしてください。");
            bool enabled = ctx.Plugin.EventLogger.Enabled;
            if (ImGui.Checkbox("クリックをemj-events.logへ記録", ref enabled))
            {
                ctx.Plugin.EventLogger.Enabled = enabled;
                if (enabled)
                    ctx.Plugin.EventLogger.OpenLog();
                else
                    ctx.Plugin.EventLogger.CloseLog();
                ctx.LastToast = enabled ? "イベントログ有効" : "イベントログ無効";
            }
            DevHelpers.CopyButton(ctx.Plugin.EventLogger.LogPath, "eventlog");
            ImGui.SameLine(0, 6);
            ImGui.AlignTextToFramePadding();
            Theme.Subtle(ctx.Plugin.EventLogger.LogPath);
        }
    }

    private void DrawDiscardCaptureCard()
    {
        using (Theme.BeginCard("diag-discardhook"))
        {
            Theme.SectionHeader("打牌取得");
            Theme.Subtle("確定した打牌を即時取得します。起動時に方式を自動選択し、ネイティブフックを優先、アドオン監視を代替として使用します。");
            var c = ctx.Plugin.DiscardCapture;
            DevHelpers.KeyValueRow("方式", c.StrategyName);
            DevHelpers.KeyValueRow("状態", c.Health.ToString());
            DevHelpers.KeyValueRow("取得合計", c.TotalCaptured.ToString());
            DevHelpers.KeyValueRow("最終牌ID", c.LastTileId.ToString());
            DevHelpers.KeyValueRow("ログ", ctx.Plugin.DiscardCaptureLogger.LogPath);
            DevHelpers.CopyButton(ctx.Plugin.DiscardCaptureLogger.LogPath, "discardlog");
        }
    }

    private void DrawTelemetryCard()
    {
        using (Theme.BeginCard("diag-telemetry"))
        {
            Theme.SectionHeader("テレメトリ");
            Theme.Subtle("エラー・検出情報をプロジェクトの調査用エンドポイントへ匿名送信します。URLは起動時にGitHubから取得します。");

            var ep = ctx.Plugin.TelemetryUploader.CurrentEndpoint;
            DevHelpers.KeyValueRow("送信先", string.IsNullOrEmpty(ep.UploadUrl) ? "(none)" : ep.UploadUrl);
            DevHelpers.KeyValueRow("有効", ep.Enabled.ToString());
            if (!string.IsNullOrEmpty(ep.MinPluginVersion))
                DevHelpers.KeyValueRow("最小プラグイン版", ep.MinPluginVersion);

            int pending = ctx.Plugin.TelemetryUploader.CountPending();
            DevHelpers.KeyValueRow("送信待ちファイル", pending.ToString());
            if (!string.IsNullOrEmpty(ep.UploadUrl))
                DevHelpers.CopyButton(ep.UploadUrl, "telemetry", $"Copy upload URL to clipboard:\n{ep.UploadUrl}");
        }
    }

    private void DrawStreamsCard()
    {
        using (Theme.BeginCard("diag-streams"))
        {
            Theme.SectionHeader("エラーと検出情報（末尾）");
            Theme.Subtle($"Last {TailLines} lines of today's errors and findings NDJSON. Click Open to inspect the full file.");

            string configDir = Plugin.PluginInterface.GetPluginConfigDirectory();
            string errorsPath = Path.Combine(configDir, "errors", $"errors-{DateTime.UtcNow:yyyyMMdd}.ndjson");
            string findingsPath = Path.Combine(configDir, "findings", $"findings-{DateTime.UtcNow:yyyyMMdd}.ndjson");

            DrawTail("エラー", errorsPath);
            ImGui.Dummy(new Vector2(0, 4));
            DrawTail("検出情報", findingsPath);
        }
    }

    private void DrawHandOverlayDebugCard()
    {
        using (Theme.BeginCard("diag-handoverlay"))
        {
            Theme.SectionHeader("手牌オーバーレイデバッグ");
            Theme.Subtle("牌サイズに一致するすべての表示ノードをNodeList順で枠線表示します。手牌抽出前の検出状態を確認できます。");
            bool on = ctx.Plugin.HandOverlay.DebugDrawAllRects;
            if (ImGui.Checkbox("検出したすべての牌領域を枠線表示", ref on))
                ctx.Plugin.HandOverlay.DebugDrawAllRects = on;
        }
    }

    private static void DrawTail(string label, string path)
    {
        bool exists = File.Exists(path);
        if (exists)
        {
            string folder = Path.GetDirectoryName(path) ?? path;
            DevHelpers.OpenFolderButton(folder, $"tail-{label}", $"Open folder:\n{folder}");
            ImGui.SameLine(0, 3);
            DevHelpers.CopyButton(path, $"tail-{label}", $"Copy path to clipboard:\n{path}");
            ImGui.SameLine(0, 8);
            ImGui.AlignTextToFramePadding();
        }
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Header);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Faint);
        ImGui.TextUnformatted(exists ? $"({Path.GetFileName(path)})" : "ファイル未作成");
        ImGui.PopStyleColor();

        var tail = ReadTail(path, TailLines);
        if (tail.Count == 0)
        {
            Theme.Subtle("  （空）");
            return;
        }
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
        foreach (var line in tail)
        {
            string truncated = line.Length > 200 ? line[..200] + "…" : line;
            ImGui.TextWrapped("  " + truncated);
        }
        ImGui.PopStyleColor();
    }

    /// <summary>Read the last <paramref name="n"/> lines of an NDJSON file; tolerant of missing files and other readers.</summary>
    private static List<string> ReadTail(string path, int n)
    {
        var lines = new List<string>(n);
        try
        {
            if (!File.Exists(path))
                return lines;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var ring = new Queue<string>(n);
            while (sr.ReadLine() is { } line)
            {
                if (ring.Count == n)
                    ring.Dequeue();
                ring.Enqueue(line);
            }
            lines.AddRange(ring);
        }
        catch { }
        return lines;
    }
}
