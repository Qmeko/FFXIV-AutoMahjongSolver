using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Mahjong.Plugin.Dalamud.UI.DebugTabs;

/// <summary>Snap/autosnap/capture/variant-dump — the bug-report capture surface.</summary>
internal sealed class BugReportTab
{
    private readonly DevConsoleContext ctx;
    private string snapLabel = "report";
    private string captureLabel = "click";

    public BugReportTab(DevConsoleContext ctx) => this.ctx = ctx;

    public void Draw()
    {
        var cmd = ctx.Plugin.MjAutoCommand;
        string configDir = Plugin.PluginInterface.GetPluginConfigDirectory();

        using (Theme.BeginCard("br-snap"))
        {
            Theme.SectionHeader("スナップショット（1回取得）");
            Theme.Subtle("アドオンとエージェントの診断情報をラベル付きファイルへ1回保存します。ラベルには英数字、ハイフン、アンダースコアを使用できます。");

            if (!IsValidLabel(snapLabel))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
                ImGui.TextUnformatted("ラベルは半角英数字、ハイフン、アンダースコアのみ使用できます。");
                ImGui.PopStyleColor();
            }

            ImGui.SetNextItemWidth(220);
            ImGui.InputText("##snaplbl", ref snapLabel, 64);
            ImGui.SameLine(0, 8);
            using (DevHelpers.Disable(!IsValidLabel(snapLabel)))
            {
                if (ImGui.Button("取得"))
                {
                    var label = snapLabel;
                    cmd.HandleSnap(label);
                    ctx.LastToast = $"snap '{label}' queued → see chat for path";
                }
            }
            Theme.Subtle($"プラグイン設定フォルダーにsnap-LABEL-ts.txtを保存します。");
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("br-autosnap"))
        {
            Theme.SectionHeader("自動スナップショット（連続取得）");
            Theme.Subtle("ゲーム状態が変化するたびに診断情報を保存します。不具合発生時点を予測できない場合に使用します。上限は500ファイルで、同一状態は保存しません。");
            bool on = cmd.IsAutoSnapOn;

            DevHelpers.KeyValueRow("状態", on ? "ON" : "OFF");
            DevHelpers.KeyValueRow("Count", $"{cmd.AutoSnapCount} / {cmd.AutoSnapMaxCountValue}");

            ImGui.Dummy(new Vector2(0, 2));
            if (on)
            {
                if (ImGui.Button("自動取得を停止"))
                {
                    cmd.HandleAutoSnap("off");
                    ctx.LastToast = "autosnap OFF";
                }
            }
            else
            {
                if (ImGui.Button("自動取得を開始"))
                {
                    cmd.HandleAutoSnap("on");
                    ctx.LastToast = "autosnap ON — files: snap-auto-NNN-<ts>.txt";
                }
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("br-capture"))
        {
            Theme.SectionHeader("クリック取得（待機して記録）");
            Theme.Subtle("次に麻雀UIで行うクリックをラベル付きで記録します。名前を入力して「待機開始」を押し、ゲーム内でクリックしてください。1回のクリックまたは60秒後に自動解除されます。");
            var pending = ctx.Plugin.EventLogger.PendingCaptureLabel;

            if (pending is not null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
                ImGui.TextUnformatted($"ARMED: '{pending}' — click the action in-game");
                ImGui.PopStyleColor();
                ImGui.Dummy(new Vector2(0, 2));
                if (ImGui.Button("待機解除"))
                {
                    ctx.Plugin.EventLogger.DisarmCapture();
                    ctx.LastToast = $"capture disarmed (was: {pending})";
                }
            }
            else
            {
                ImGui.SetNextItemWidth(220);
                ImGui.InputText("##caplbl", ref captureLabel, 64);
                ImGui.SameLine(0, 8);
                bool armBlocked = !IsValidLabel(captureLabel) || ctx.Plugin.Configuration.AutomationArmed;
                using (DevHelpers.Disable(armBlocked))
                {
                    if (ImGui.Button("待機開始"))
                    {
                        cmd.HandleCapture(captureLabel);
                        ctx.LastToast = $"capture armed: '{captureLabel}' — auto-disarms after one click or 60s";
                    }
                }
                if (ctx.Plugin.Configuration.AutomationArmed)
                    Theme.Subtle("自動プレイが有効です。操作が競合するため、先に自動操作を停止してください。");
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("br-variant"))
        {
            Theme.SectionHeader("バリアント情報の出力（新クライアント報告）");
            Theme.Subtle("ゲーム更新後に手牌や盤面を読み取れない場合に実行します。新しいクライアント差異を報告するためのファイルを1つ出力します。");
            if (ImGui.Button("バリアントを出力"))
            {
                cmd.DumpVariant();
                ctx.LastToast = "variant dump queued → emj-variant-dump.txt";
            }
            ImGui.SameLine(0, 6);
            DevHelpers.CopyPathButton("emj-variant-dump.txt", "variant");
            Theme.Subtle("新しいクライアント差異を報告する際はemj-variant-dump.txtを添付してください。");
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("br-folder"))
        {
            Theme.SectionHeader("ファイル");
            Theme.Subtle("診断ファイルの保存フォルダーを開きます。GitHub Issueへドラッグ＆ドロップして添付できます。");

            DrawDirRow("プラグイン設定", configDir, "cfgdir");
            string findingsDir = Path.Combine(configDir, "findings");
            if (Directory.Exists(findingsDir))
                DrawDirRow("検出情報",      findingsDir, "findingsdir");
        }

        ImGui.Dummy(new Vector2(0, 4));
        DrawRecentDumpsCard(configDir);
    }

    /// <summary>Icon pair (open + copy) followed by an aligned "label: path" line for a directory.</summary>
    private static void DrawDirRow(string label, string path, string id)
    {
        DevHelpers.OpenFolderButton(path, id, $"Open folder:\n{path}");
        ImGui.SameLine(0, 3);
        DevHelpers.CopyButton(path, id, $"Copy path to clipboard:\n{path}");
        ImGui.SameLine(0, 8);
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.TextUnformatted($"{label}:");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Faint);
        ImGui.TextWrapped(path);
        ImGui.PopStyleColor();
    }

    /// <summary>Last 10 generated dump files; click to reveal in explorer or copy path.</summary>
    private static void DrawRecentDumpsCard(string configDir)
    {
        using (Theme.BeginCard("br-recent"))
        {
            Theme.SectionHeader("最近の診断ファイル");
            Theme.Subtle("最近作成された診断ファイルです。「開く」で保存場所を表示し、「コピー」で完全なパスをクリップボードへ保存します。");

            var files = RecentDumps(configDir, 10);
            if (files.Count == 0)
            {
                Theme.Subtle("診断ファイルはまだありません");
                return;
            }

            for (int i = 0; i < files.Count; i++)
            {
                var (path, when) = files[i];
                string name = Path.GetFileName(path);
                string age = FormatAge(DateTime.UtcNow - when);
                string folder = Path.GetDirectoryName(path) ?? configDir;

                DevHelpers.OpenFolderButton(folder, $"rd{i}", $"Reveal in folder:\n{folder}");
                ImGui.SameLine(0, 3);
                DevHelpers.CopyButton(path, $"rd{i}", $"Copy path to clipboard:\n{path}");
                ImGui.SameLine(0, 8);

                ImGui.AlignTextToFramePadding();
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
                ImGui.TextUnformatted(name);
                ImGui.PopStyleColor();
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Faint);
                ImGui.TextUnformatted($"  {age} ago");
                ImGui.PopStyleColor();
            }
        }
    }

    private static List<(string Path, DateTime WrittenUtc)> RecentDumps(string configDir, int max)
    {
        var result = new List<(string, DateTime)>();
        try
        {
            if (!Directory.Exists(configDir))
                return result;
            string[] patterns = { "snap-*.txt", "emj-*.txt" };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pat in patterns)
            {
                foreach (var f in Directory.EnumerateFiles(configDir, pat, SearchOption.TopDirectoryOnly))
                {
                    if (!seen.Add(f))
                        continue;
                    result.Add((f, File.GetLastWriteTimeUtc(f)));
                }
            }
            result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            if (result.Count > max)
                result.RemoveRange(max, result.Count - max);
        }
        catch { }
        return result;
    }

    private static string FormatAge(TimeSpan d)
    {
        if (d.TotalSeconds < 60)
            return $"{(int)d.TotalSeconds}s";
        if (d.TotalMinutes < 60)
            return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24)
            return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }

    private static bool IsValidLabel(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        foreach (var c in s)
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        return true;
    }
}
