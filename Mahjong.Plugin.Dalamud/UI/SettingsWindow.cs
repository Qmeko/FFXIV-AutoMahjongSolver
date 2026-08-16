using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Mahjong.Plugin.Dalamud.ExternalAi;

namespace Mahjong.Plugin.Dalamud.UI;

/// <summary>Settings, grouped into Auto-play / Appearance / Developer cards.</summary>
public sealed class SettingsWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    /// <summary>Which color the preview row renders — purely UI state, not persisted.</summary>
    private bool previewShowsTsumogiri;

    public SettingsWindow(Plugin plugin)
        : base("ドマ式麻雀ソルバー デバッグ版 — 設定###domanmahjong-debug-settings")
    {
        ArgumentNullException.ThrowIfNull(plugin);
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(480, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(800, 1200),
        };
    }

    public void Dispose() { }

    private static string StyleLabel(HighlightStyle s) => s switch
    {
        HighlightStyle.Arrow => "大きな矢印＋ラベル",
        _ => "ネオン発光＋コーナー枠",
    };

    private static string ProviderLabel(AiProvider provider) => provider switch
    {
        AiProvider.BundledMortal => "Mortal 298k（VoidShine）",
        AiProvider.BundledAkochan => "Akochan（インストール済みランタイム）",
        AiProvider.ExternalMjai => "外部mjaiプロセス",
        _ => "内蔵ヒューリスティック",
    };

    private static string AkochanProfileLabel(AkochanInferenceProfile profile) => profile switch
    {
        AkochanInferenceProfile.Precision => "精度優先",
        _ => "高速（リアルタイム）",
    };

    private void DrawHighlightPreview(Configuration cfg)
    {
        // Radio above the preview to flip which color is being shown.
        bool showDiscard = !previewShowsTsumogiri;
        if (ImGui.RadioButton("プレビュー: 打牌", showDiscard))
            previewShowsTsumogiri = false;
        ImGui.SameLine(0, 14);
        if (ImGui.RadioButton("ツモ切り", !showDiscard))
            previewShowsTsumogiri = true;

        // Reserve a canvas wide enough for 5 fake tiles + headroom for the arrow/label.
        const int tileCount = 5;
        const float tileW = 34f;
        const float tileH = 50f;
        const float tileGap = 6f;
        const float topPad = 56f;   // room above for the arrow/pill
        const float botPad = 12f;

        float canvasW = ImGui.GetContentRegionAvail().X;
        float canvasH = topPad + tileH + botPad;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(canvasW, canvasH));

        var dl = ImGui.GetWindowDrawList();

        // Backing panel so the preview reads as its own surface.
        dl.AddRectFilled(origin, origin + new Vector2(canvasW, canvasH), Theme.Pack(Theme.SurfaceAlt), 6f);
        dl.AddRect(origin, origin + new Vector2(canvasW, canvasH), Theme.Pack(Theme.Border), 6f, ImDrawFlags.None, 1f);

        // Lay out 5 fake tiles centered horizontally.
        float rowW = tileCount * tileW + (tileCount - 1) * tileGap;
        float startX = origin.X + (canvasW - rowW) * 0.5f;
        float tileY = origin.Y + topPad;
        var rects = new List<(Vector2 Pos, Vector2 Size)>(tileCount);
        for (int i = 0; i < tileCount; i++)
        {
            var pos = new Vector2(startX + i * (tileW + tileGap), tileY);
            var size = new Vector2(tileW, tileH);
            rects.Add((pos, size));
            // Shadow + tile face + border, same recipe as Theme.DrawTile but without an item dummy.
            dl.AddRectFilled(pos + new Vector2(1, 2), pos + size + new Vector2(1, 2), Theme.Pack(Theme.TileShadow), 4f);
            dl.AddRectFilled(pos, pos + size, Theme.Pack(Theme.TileFace), 4f);
            dl.AddRect(pos, pos + size, Theme.Pack(Theme.TileBorder), 4f, ImDrawFlags.None, 1.5f);
        }

        int pickSlot = tileCount / 2;
        bool isDrawnTile = previewShowsTsumogiri;
        var color = (isDrawnTile ? cfg.HighlightColorTsumogiri : cfg.HighlightColorDiscard).ToVector3();
        float intensity = Math.Clamp(cfg.HighlightIntensity, 0.4f, 1.6f);

        // Clip to the preview panel so the arrow/glow can't escape it.
        dl.PushClipRect(origin, origin + new Vector2(canvasW, canvasH), true);
        switch (cfg.HighlightStyle)
        {
            case HighlightStyle.Arrow:
                HandOverlay.DrawHighlightArrow(dl, rects[pickSlot], color, intensity, isDrawnTile);
                break;
            default:
                HandOverlay.DrawHighlightNeonGlow(dl, rects[pickSlot], color, intensity);
                break;
        }
        dl.PopClipRect();
    }

    private static void DrawDelayRange(string label, int currentMin, int currentMax, Action<int, int> update)
    {
        int min = currentMin;
        int max = currentMax;
        ImGui.SetNextItemWidth(145);
        if (ImGui.SliderInt($"{label} min##{label}", ref min, 0, 5000, "%d ms"))
        {
            min = Math.Min(min, max);
            update(min, max);
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(145);
        if (ImGui.SliderInt($"max##{label}", ref max, 0, 5000, "%d ms"))
        {
            max = Math.Max(max, min);
            update(min, max);
        }
    }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        using var _s = Theme.PushWindowStyle();

        using (Theme.BeginCard("settings-play"))
        {
            Theme.SectionHeader("自動プレイ");

            bool humanDelay = cfg.HumanDelayEnabled;
            if (ImGui.Checkbox("人間らしい操作遅延", ref humanDelay))
                plugin.ConfigService.Update(c => c with { HumanDelayEnabled = humanDelay });
            Theme.Subtle("Mortalは即座に判断し、操作だけを人間らしい間隔まで遅延します。15秒の制限時間が近づくと遅延を短縮します。");

            if (!humanDelay)
                ImGui.BeginDisabled();

            DrawDelayRange("通常打牌", cfg.DiscardDelayMinMs, cfg.DiscardDelayMaxMs,
                (min, max) => plugin.ConfigService.Update(c => c with { DiscardDelayMinMs = min, DiscardDelayMaxMs = max }));
            DrawDelayRange("ツモ切り", cfg.TsumogiriDelayMinMs, cfg.TsumogiriDelayMaxMs,
                (min, max) => plugin.ConfigService.Update(c => c with { TsumogiriDelayMinMs = min, TsumogiriDelayMaxMs = max }));
            DrawDelayRange("リーチ", cfg.RiichiDelayMinMs, cfg.RiichiDelayMaxMs,
                (min, max) => plugin.ConfigService.Update(c => c with { RiichiDelayMinMs = min, RiichiDelayMaxMs = max }));
            DrawDelayRange("和了", cfg.WinDelayMinMs, cfg.WinDelayMaxMs,
                (min, max) => plugin.ConfigService.Update(c => c with { WinDelayMinMs = min, WinDelayMaxMs = max }));

            int emergency = cfg.EmergencyImmediateThresholdMs;
            ImGui.SetNextItemWidth(300);
            if (ImGui.SliderInt("緊急時の即時実行しきい値", ref emergency, 1000, 5000, "%d ms"))
                plugin.ConfigService.Update(c => c with { EmergencyImmediateThresholdMs = emergency });
            Theme.Subtle("推定残り時間がこの値以下になると、操作を即時実行します。");

            if (!humanDelay)
                ImGui.EndDisabled();

            ImGui.Dummy(new Vector2(0, 8));
            Theme.SectionHeader("自動鳴き");

            bool autoCall = cfg.AutoCallEnabled;
            if (ImGui.Checkbox("自動鳴きを有効にする", ref autoCall))
                plugin.ConfigService.Update(c => c with { AutoCallEnabled = autoCall });
            Theme.Subtle("無効時もヒントは表示されます。自動承諾だけを停止します。");

            if (!autoCall)
                ImGui.BeginDisabled();

            bool autoPass = cfg.AutoPassEnabled;
            if (ImGui.Checkbox("パス", ref autoPass))
                plugin.ConfigService.Update(c => c with { AutoPassEnabled = autoPass });
            DrawDelayRange("パス遅延", cfg.PassDelayMinMs, cfg.PassDelayMaxMs,
                (min, max) => plugin.ConfigService.Update(c => c with { PassDelayMinMs = min, PassDelayMaxMs = max }));

            bool autoPon = cfg.AutoPonEnabled;
            if (ImGui.Checkbox("ポン", ref autoPon))
                plugin.ConfigService.Update(c => c with { AutoPonEnabled = autoPon });
            DrawDelayRange("ポン遅延", cfg.PonDelayMinMs, cfg.PonDelayMaxMs,
                (min, max) => plugin.ConfigService.Update(c => c with { PonDelayMinMs = min, PonDelayMaxMs = max }));

            bool autoChi = cfg.AutoChiEnabled;
            if (ImGui.Checkbox("チー", ref autoChi))
                plugin.ConfigService.Update(c => c with { AutoChiEnabled = autoChi });
            DrawDelayRange("チー遅延", cfg.ChiDelayMinMs, cfg.ChiDelayMaxMs,
                (min, max) => plugin.ConfigService.Update(c => c with { ChiDelayMinMs = min, ChiDelayMaxMs = max }));

            bool autoMinKan = cfg.AutoMinKanEnabled;
            if (ImGui.Checkbox("大明槓", ref autoMinKan))
                plugin.ConfigService.Update(c => c with { AutoMinKanEnabled = autoMinKan });
            DrawDelayRange("大明槓遅延", cfg.MinKanDelayMinMs, cfg.MinKanDelayMaxMs,
                (min, max) => plugin.ConfigService.Update(c => c with { MinKanDelayMinMs = min, MinKanDelayMaxMs = max }));

            bool autoAnKan = cfg.AutoAnKanEnabled;
            if (ImGui.Checkbox("暗槓", ref autoAnKan))
                plugin.ConfigService.Update(c => c with { AutoAnKanEnabled = autoAnKan });
            DrawDelayRange("暗槓遅延", cfg.AnKanDelayMinMs, cfg.AnKanDelayMaxMs,
                (min, max) => plugin.ConfigService.Update(c => c with { AnKanDelayMinMs = min, AnKanDelayMaxMs = max }));

            bool autoShouMinKan = cfg.AutoShouMinKanEnabled;
            if (ImGui.Checkbox("加槓", ref autoShouMinKan))
                plugin.ConfigService.Update(c => c with { AutoShouMinKanEnabled = autoShouMinKan });
            DrawDelayRange("加槓遅延", cfg.ShouMinKanDelayMinMs, cfg.ShouMinKanDelayMaxMs,
                (min, max) => plugin.ConfigService.Update(c => c with { ShouMinKanDelayMinMs = min, ShouMinKanDelayMaxMs = max }));

            if (!autoCall)
                ImGui.EndDisabled();

            Theme.Subtle("各操作は個別に有効・無効と遅延を設定できます。ヒントモードではパスのみ有効にすると、暗槓画面でも自動打牌しません。");

            ImGui.Dummy(new Vector2(0, 8));

            bool autoAdvance = cfg.AutoAdvanceAfterHand;
            if (ImGui.Checkbox("各局終了後に自動で次へ進む", ref autoAdvance))
                plugin.ConfigService.Update(c => c with { AutoAdvanceAfterHand = autoAdvance });
            Theme.Subtle("和了・流局結果画面の「次へ」を自動選択し、次局を開始します。");
        }

        ImGui.Dummy(new Vector2(0, 4));

        using (Theme.BeginCard("settings-ai"))
        {
            Theme.SectionHeader("判断AI");

            var provider = cfg.AiProvider;
            string providerLabel = ProviderLabel(provider);
            ImGui.SetNextItemWidth(300);
            if (ImGui.BeginCombo("AIプロバイダー", providerLabel))
            {
                foreach (var option in new[] { AiProvider.BundledMortal, AiProvider.BundledAkochan, AiProvider.BuiltIn, AiProvider.ExternalMjai })
                {
                    bool selected = option == provider;
                    if (ImGui.Selectable(ProviderLabel(option), selected) && !selected)
                    {
                        plugin.ConfigService.Update(c => c with { AiProvider = option });
                        if (plugin.Policy is SelectablePolicy selectablePolicy)
                            selectablePolicy.SelectProvider(option);
                        if (option == AiProvider.BundledMortal)
                            plugin.MortalInstaller.StartIfNeeded();
                        plugin.Aggregator.RefreshDecision();
                    }
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            Theme.Subtle(provider == AiProvider.BundledMortal
                ? "VoidShine/mortal-298kをMortalランタイムで起動して判断に使用します。ほかのAIは停止します。"
                : provider == AiProvider.BundledAkochan
                ? "Akochanのみを起動して判断に使用します。ほかのAIは停止します。"
                : provider == AiProvider.BuiltIn
                ? "内蔵AIのみを使用します。外部AIプロセスは起動しません。"
                : "指定した外部MJAIのみを起動して判断に使用します。");

            if (provider == AiProvider.BundledAkochan)
            {
                ImGui.Dummy(new Vector2(0, 4));
                var profile = cfg.AkochanInferenceProfile;
                ImGui.SetNextItemWidth(300);
                if (ImGui.BeginCombo("Akochan推論モード", AkochanProfileLabel(profile)))
                {
                    foreach (var option in new[] { AkochanInferenceProfile.Realtime, AkochanInferenceProfile.Precision })
                    {
                        bool selected = option == profile;
                        if (ImGui.Selectable(AkochanProfileLabel(option), selected) && !selected)
                        {
                            plugin.ConfigService.Update(c => c with { AkochanInferenceProfile = option });
                            plugin.Aggregator.RefreshDecision();
                        }
                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
                Theme.Subtle(profile == AkochanInferenceProfile.Realtime
                    ? "Akochan公式の軽量探索設定を使用します。通常の打牌推論を短縮します。"
                    : "従来の探索設定を使用します。推論時間より探索量を優先します。");
            }

            if (provider == AiProvider.BundledMortal)
            {
                ImGui.Dummy(new Vector2(0, 4));
                var install = plugin.MortalInstaller;
                Theme.Subtle("セットアップ: " + install.StatusText);
                if (install.State == MortalRuntimeInstaller.RuntimeInstallState.Failed
                    && ImGui.Button("Mortalセットアップを再試行", new Vector2(260, 0)))
                {
                    install.StartIfNeeded();
                }
                if (install.State == MortalRuntimeInstaller.RuntimeInstallState.Installing)
                    Theme.Subtle("初回のみ Python / PyTorch / モデルを自動ダウンロードします。完了まで待ってください。");

                ImGui.Dummy(new Vector2(0, 4));
                bool online = cfg.MortalOnline;
                if (ImGui.Checkbox("Mortalオンライン推論を使用", ref online))
                    plugin.ConfigService.Update(c => c with { MortalOnline = online });
                Theme.Subtle("無効時はローカルのVoidShine/mortal-298k（四人南・298,000ステップ）を使用します。");

                if (online)
                {
                    string server = cfg.MortalServer;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText("サーバー", ref server, 1024))
                        plugin.ConfigService.Update(c => c with { MortalServer = server });

                    string apiKey = cfg.MortalApiKey;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText("APIキー", ref apiKey, 1024, ImGuiInputTextFlags.Password))
                        plugin.ConfigService.Update(c => c with { MortalApiKey = apiKey });
                }

                int startupTimeout = cfg.ExternalAiStartupTimeoutMs;
                ImGui.SetNextItemWidth(300);
                if (ImGui.SliderInt("モデル起動タイムアウト", ref startupTimeout, 10000, 180000, "%d ms"))
                    plugin.ConfigService.Update(c => c with { ExternalAiStartupTimeoutMs = startupTimeout });

                int threads = cfg.MortalCpuThreads;
                ImGui.SetNextItemWidth(300);
                if (ImGui.SliderInt("Mortal CPUスレッド数", ref threads, 1, Math.Max(1, Math.Min(Environment.ProcessorCount, 16)), "%d"))
                    plugin.ConfigService.Update(c => c with { MortalCpuThreads = threads });
                Theme.Subtle("値を小さくするとゲーム側とのCPU競合を軽減できます。変更後はプラグインを再起動してください。");

                bool autoRestart = cfg.MortalAutoRestart;
                if (ImGui.Checkbox("障害発生後にMortalを自動再起動", ref autoRestart))
                    plugin.ConfigService.Update(c => c with { MortalAutoRestart = autoRestart });
            }

            if (provider == AiProvider.ExternalMjai)
            {
                ImGui.Dummy(new Vector2(0, 4));
                string executable = cfg.ExternalAiExecutable;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("実行ファイル", ref executable, 1024))
                    plugin.ConfigService.Update(c => c with { ExternalAiExecutable = executable });

                string arguments = cfg.ExternalAiArguments;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("引数", ref arguments, 2048))
                    plugin.ConfigService.Update(c => c with { ExternalAiArguments = arguments });

                string working = cfg.ExternalAiWorkingDirectory;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("作業フォルダー", ref working, 1024))
                    plugin.ConfigService.Update(c => c with { ExternalAiWorkingDirectory = working });
            }

            if (provider != AiProvider.BuiltIn)
            {
                int timeout = cfg.ExternalAiTimeoutMs;
                ImGui.SetNextItemWidth(300);
                if (ImGui.SliderInt("応答タイムアウト", ref timeout, 500, 15000, "%d ms"))
                    plugin.ConfigService.Update(c => c with { ExternalAiTimeoutMs = timeout });

                Theme.Subtle("選択AI専用モード: エラー時も別のAIへ切り替えません。");

                if (plugin.Policy is SelectablePolicy selectable)
                {
                    string selectedStatus = provider == AiProvider.BundledAkochan
                        ? selectable.AkochanStatus
                        : selectable.ExternalStatus;
                    long selectedInference = provider == AiProvider.BundledAkochan
                        ? selectable.AkochanInferenceMs
                        : selectable.LastInferenceMs;
                    int selectedRestarts = provider == AiProvider.BundledAkochan
                        ? selectable.AkochanRestartCount
                        : selectable.RestartCount;
                    Theme.Subtle($"Status: {selectedStatus}");
                    Theme.Subtle($"Decision source: {selectable.LastDecisionSource}");
                    Theme.Subtle($"Last inference: {selectedInference} ms | Restarts: {selectedRestarts}");
                    if (!string.IsNullOrWhiteSpace(selectable.LastFallbackReason))
                        Theme.Subtle($"Fallback reason: {selectable.LastFallbackReason}");

                    ImGui.Dummy(new Vector2(0, 4));
                    if (ImGui.Button("AI局面を強制再同期", new Vector2(220, 0)))
                        plugin.ForceAiResync("manual-settings-button");
                    Theme.Subtle("選択中AIのセッションを破棄し、現在の盤面から判断を作り直します。局開始からの完全な時系列が取得できない場合は、現在盤面を基準に安全な再構築を行います。");
                }
                else
                {
                    Theme.Subtle("状態: 利用不可");
                }
            }
        }

        ImGui.Dummy(new Vector2(0, 4));

        using (Theme.BeginCard("settings-appearance"))
        {
            Theme.SectionHeader("表示");

            bool highlight = cfg.ShowInGameHighlight;
            if (ImGui.Checkbox("推奨牌を麻雀画面上で強調表示", ref highlight))
                plugin.ConfigService.Update(c => c with { ShowInGameHighlight = highlight });
            Theme.Subtle("打牌候補を点滅する枠線で表示します。ヒントモードで使用されます。");

            ImGui.Dummy(new Vector2(0, 4));

            if (!highlight)
                ImGui.BeginDisabled();

            var style = cfg.HighlightStyle;
            ImGui.SetNextItemWidth(300);
            if (ImGui.BeginCombo("強調表示スタイル", StyleLabel(style)))
            {
                foreach (var opt in new[] { HighlightStyle.NeonGlow, HighlightStyle.Arrow })
                {
                    bool selected = opt == style;
                    if (ImGui.Selectable(StyleLabel(opt), selected) && opt != style)
                        plugin.ConfigService.Update(c => c with { HighlightStyle = opt });
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            Theme.Subtle(style switch
            {
                HighlightStyle.Arrow => "「打牌／ツモ切り」ラベル付きの大きな矢印を表示します。牌画像を覆いません。",
                _ => "ネオン発光、L字型コーナー枠、牌の上で上下する矢印を表示します。",
            });

            ImGui.Dummy(new Vector2(0, 6));

            DrawHighlightPreview(cfg);

            ImGui.Dummy(new Vector2(0, 6));

            // Discard color.
            var discardVec = cfg.HighlightColorDiscard.ToVector3();
            if (ImGui.ColorEdit3("打牌色", ref discardVec,
                    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                plugin.ConfigService.Update(c => c with { HighlightColorDiscard = RgbColor.From(discardVec) });

            // Tsumogiri color.
            var tsuVec = cfg.HighlightColorTsumogiri.ToVector3();
            if (ImGui.ColorEdit3("ツモ切り色", ref tsuVec,
                    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                plugin.ConfigService.Update(c => c with { HighlightColorTsumogiri = RgbColor.From(tsuVec) });

            // Call (chi/pon/kan) tile-set color.
            var callVec = cfg.HighlightColorCall.ToVector3();
            if (ImGui.ColorEdit3("鳴き色（チー・ポン・カンで使う牌）", ref callVec,
                    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                plugin.ConfigService.Update(c => c with { HighlightColorCall = RgbColor.From(callVec) });

            // Intensity slider.
            float intensity = cfg.HighlightIntensity;
            ImGui.SetNextItemWidth(300);
            if (ImGui.SliderFloat("強度", ref intensity, 0.4f, 1.6f, "%.2fx"))
                plugin.ConfigService.Update(c => c with { HighlightIntensity = intensity });

            // Reset button.
            if (ImGui.SmallButton("オーバーレイの色と強度を初期化"))
                plugin.ConfigService.Update(c => c with
                {
                    HighlightColorDiscard = RgbColor.Defaults.Discard,
                    HighlightColorTsumogiri = RgbColor.Defaults.Tsumogiri,
                    HighlightColorCall = RgbColor.Defaults.Call,
                    HighlightIntensity = 1.0f,
                });

            if (!highlight)
                ImGui.EndDisabled();

            ImGui.Dummy(new Vector2(0, 4));

            bool details = cfg.ShowSuggestionDetails;
            if (ImGui.Checkbox("最善手の下に分析詳細を表示", ref details))
                plugin.ConfigService.Update(c => c with { ShowSuggestionDetails = details });
            Theme.Subtle("メイン画面に上位打牌候補のシャンテン数と受け入れを表示します。");
        }

        ImGui.Dummy(new Vector2(0, 4));

        using (Theme.BeginCard("settings-dev"))
        {
            Theme.SectionHeader("開発者");

            bool dev = cfg.DevMode;
            if (ImGui.Checkbox("開発者ツールを有効にする", ref dev))
            {
                plugin.ConfigService.Update(c => c with { DevMode = dev });
                if (dev)
                    plugin.DebugOverlay.IsOpen = true;
            }
            Theme.Subtle("メイン画面のツールバーにデバッグボタンを追加します。");

            ImGui.Dummy(new Vector2(0, 4));
            bool diagnosticLog = cfg.DiagnosticDecisionLogging;
            if (ImGui.Checkbox("判断診断ログ（/xllog）", ref diagnosticLog))
                plugin.ConfigService.Update(c => c with { DiagnosticDecisionLogging = diagnosticLog });
            Theme.Subtle("判断要求時のみMahjongSnapshot、MortalInput、MortalDecisionを記録します。");
        }
    }
}
