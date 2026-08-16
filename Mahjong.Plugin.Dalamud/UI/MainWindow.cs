using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Mahjong.Engine;
using Mahjong.Policy;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.UI;

/// <summary>End-user window: toolbar, status, mode, live game. Settings and debug live in their own windows.</summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("ドマ式麻雀ソルバー デバッグ版###domanmahjong-debug-main")
    {
        this.plugin = plugin;
        Size = new Vector2(520, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 420),
            MaximumSize = new Vector2(900, 2000),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        using var _s = Theme.PushWindowStyle();

        if (!cfg.TosAccepted)
        {
            DrawTosGate(cfg);
            return;
        }

        DrawModeCard(cfg);
        ImGui.Dummy(new Vector2(0, 4));
        DrawLiveCard();

        DrawAutoPlayConfirmModal(cfg);
    }

    private void DrawTosGate(Configuration cfg)
    {
        using (Theme.BeginCard("tos"))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Header);
            ImGui.TextUnformatted("ドマ式麻雀ソルバー デバッグ版へようこそ");
            ImGui.PopStyleColor();
            ImGui.Dummy(new Vector2(0, 6));

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
            ImGui.TextWrapped(
                "このプラグインはゲーム画面を読み取り、ドマ式麻雀の操作を代行できます。" +
                "有効にする前に、以下を確認してください。");
            ImGui.PopStyleColor();
            ImGui.Dummy(new Vector2(0, 4));

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
            ImGui.BulletText("外部ツールによる自動操作はFFXIVの利用規約に抵触します。");
            ImGui.BulletText("使用は自己責任です。アカウントが処分される可能性があります。");
            ImGui.PopStyleColor();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.BulletText("「ヒント」モードは助言を表示するだけで、自動操作は行いません。");
            ImGui.TextWrapped(
                "  • このビルドは、クライアント差異の調査を目的として匿名化された対局ログ、エラー報告、" +
                "麻雀アドオンの診断情報を送信します。キャラクター名、コンテンツID、その他の個人情報は" +
                "含まれません。送信データはインストールごとに生成されるランダムIDでのみ識別されます。" +
                "");
            ImGui.PopStyleColor();

            ImGui.Dummy(new Vector2(0, 10));

            ImGui.PushStyleColor(ImGuiCol.Button, Theme.Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(Theme.Accent.X * 1.15f, Theme.Accent.Y * 1.15f, Theme.Accent.Z * 1.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(Theme.Accent.X * 0.85f, Theme.Accent.Y * 0.85f, Theme.Accent.Z * 0.85f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.05f, 0.10f, 0.08f, 1f));
            float btnW = ImGui.GetContentRegionAvail().X;
            if (ImGui.Button("理解して続行", new Vector2(btnW, 36)))
                plugin.ConfigService.Update(c => c with { TosAccepted = true });
            ImGui.PopStyleColor(4);
        }
    }

    private void DrawModeCard(Configuration cfg)
    {
        using (Theme.BeginCard("mode"))
        {
            // Header row: "動作モード" label + right-aligned icon actions.
            ImGui.AlignTextToFramePadding();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Header);
            ImGui.TextUnformatted("動作モード");
            ImGui.PopStyleColor();

            DrawHeaderIcons(cfg);

            var dl = ImGui.GetWindowDrawList();
            var p = ImGui.GetCursorScreenPos();
            float rw = ImGui.GetContentRegionAvail().X;
            dl.AddLine(p + new Vector2(0, 2), new Vector2(p.X + rw, p.Y + 2), Theme.Pack(Theme.Divider), 1f);
            ImGui.Dummy(new Vector2(0, 6));

            int current = !cfg.AutomationArmed ? 0 : (cfg.SuggestionOnly ? 1 : 2);
            float avail = ImGui.GetContentRegionAvail().X;
            float gap = 6f;
            float w = (avail - gap * 2) / 3f;
            var size = new Vector2(w, 50);

            if (ModePill("停止", "何もしない", Theme.Muted, current == 0, size))
                RequestMode(0, cfg);
            ImGui.SameLine(0, gap);
            if (ModePill("ヒント", "最善手を強調表示", Theme.Warn, current == 1, size))
                RequestMode(1, cfg);
            ImGui.SameLine(0, gap);
            if (ModePill("自動プレイ", "自動で操作", Theme.Accent, current == 2, size))
                RequestMode(2, cfg);
        }
    }

    private void DrawHeaderIcons(Configuration cfg)
    {
        var bugLabel = FontAwesomeIcon.Bug.ToIconString();
        var infoLabel = FontAwesomeIcon.InfoCircle.ToIconString();
        var gearLabel = FontAwesomeIcon.Cog.ToIconString();

        bool bugClicked = false, infoClicked, gearClicked;
        // Tooltips render outside the icon-font scope — they would use icon glyphs otherwise.
        int hovered = -1;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var framePadX = ImGui.GetStyle().FramePadding.X;
            var spacingX = ImGui.GetStyle().ItemSpacing.X;
            var btnW = ImGui.CalcTextSize(gearLabel).X + framePadX * 2;
            int slots = cfg.DevMode ? 3 : 2;
            float totalW = btnW * slots + spacingX * (slots - 1);
            Theme.RightAlign(totalW, Theme.CardPadX);

            if (cfg.DevMode)
            {
                bugClicked = ImGui.Button(bugLabel + "##debug");
                if (ImGui.IsItemHovered()) hovered = 0;
                ImGui.SameLine();
            }
            infoClicked = ImGui.Button(infoLabel + "##about");
            if (ImGui.IsItemHovered()) hovered = 1;
            ImGui.SameLine();
            gearClicked = ImGui.Button(gearLabel + "##settings");
            if (ImGui.IsItemHovered()) hovered = 2;
        }

        switch (hovered)
        {
            case 0: ImGui.SetTooltip("開発者コンソール"); break;
            case 1: ImGui.SetTooltip("このプラグインについて"); break;
            case 2: ImGui.SetTooltip("設定"); break;
        }

        if (bugClicked) plugin.ToggleDebugOverlay();
        if (infoClicked) plugin.ToggleAboutWindow();
        if (gearClicked) plugin.ToggleSettingsWindow();
    }

    private static bool ModePill(string title, string sub, Vector4 tint, bool selected, Vector2 size)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;

        bool clicked = ImGui.InvisibleButton($"##mode-{title}", size);
        bool hovered = ImGui.IsItemHovered();

        Vector4 bg = selected ? Theme.Fade(tint, 0.30f)
                     : hovered ? Theme.Fade(tint, 0.15f)
                               : Theme.Fade(tint, 0.07f);
        Vector4 border = selected ? tint : Theme.Fade(tint, 0.40f);

        dl.AddRectFilled(min, max, Theme.Pack(bg), 6f);
        dl.AddRect(min, max, Theme.Pack(border), 6f, ImDrawFlags.None, selected ? 2f : 1f);

        var titleSize = ImGui.CalcTextSize(title);
        var subSize = ImGui.CalcTextSize(sub);
        Vector4 titleColor = selected ? new Vector4(1f, 1f, 1f, 1f) : tint;
        Vector4 subColor = selected ? new Vector4(1f, 1f, 1f, 0.75f) : Theme.Fade(tint, 0.65f);
        var titlePos = min + new Vector2((size.X - titleSize.X) * 0.5f, 8);
        var subPos = min + new Vector2((size.X - subSize.X) * 0.5f, size.Y - subSize.Y - 6);
        dl.AddText(titlePos, Theme.Pack(titleColor), title);
        dl.AddText(subPos, Theme.Pack(subColor), sub);

        return clicked;
    }

    private bool autoPlayConfirmPending;

    private void RequestMode(int mode, Configuration cfg)
    {
        if (mode == 2 && !cfg.AutoPlayConfirmed)
        {
            autoPlayConfirmPending = true;
            return;
        }
        ApplyMode(mode);
    }

    private void ApplyMode(int mode)
    {
        bool enteringAutoPlay = mode == 2
            && (!plugin.Configuration.AutomationArmed || plugin.Configuration.SuggestionOnly);

        plugin.ConfigService.Update(c => c with
        {
            AutomationArmed = mode > 0,
            SuggestionOnly = mode == 1,
        });

        // Only the transition into auto-play changes the game's vanilla option.
        // Stop/Hint mode never changes the user's vanilla setting.
        if (enteringAutoPlay)
            plugin.VanillaAutoWin.Enable();
    }

    private void DrawAutoPlayConfirmModal(Configuration cfg)
    {
        const string id = "自動プレイを有効にしますか？##autoplay-confirm";
        if (autoPlayConfirmPending)
        {
            ImGui.OpenPopup(id);
            autoPlayConfirmPending = false;
        }

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(id, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
            return;

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
        ImGui.TextWrapped("自動プレイは、人間らしい間隔で操作を実行します。");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
        ImGui.PushTextWrapPos(360);
        ImGui.TextUnformatted(
            "停止するには、いつでも動作モードを「停止」または「ヒント」に切り替えてください。外部ツールによる自動操作は" +
            "FFXIVの利用規約に抵触します。使用は自己責任です。");
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 10));

        if (ImGui.Button("自動プレイを有効化", new Vector2(160, 28)))
        {
            plugin.ConfigService.Update(c => c with { AutoPlayConfirmed = true });
            ApplyMode(2);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("キャンセル", new Vector2(100, 28)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawLiveCard()
    {
        using (Theme.BeginCard("live"))
        {
            Theme.SectionHeader("対局状況");

            var snap = plugin.Aggregator.Latest;
            if (snap is null)
            {
                DrawEmptyLive();
                return;
            }

            ScoredDiscard[]? scored = plugin.Aggregator.LastScored;
            ActionChoice? choice = plugin.Aggregator.LastChoice;
            string? scorerError = plugin.Aggregator.LastScorerError;
            int highlightSlot = -1;
            if (choice?.DiscardTile is { } t)
                highlightSlot = plugin.AddonReader.FindRenderedHandIndexOfTile(t);

            DrawSeatRow(snap);
            ImGui.Dummy(new Vector2(0, 10));
            DrawHandRow(snap, highlightSlot);
            ImGui.Dummy(new Vector2(0, 10));
            if (plugin.Configuration.AiProvider == AiProvider.BundledAkochan)
            {
                DrawAkochanStatus();
                ImGui.Dummy(new Vector2(0, 8));
            }
            DrawSuggestion(snap, scored, choice, scorerError);
        }
    }

    private void DrawAkochanComparison(StateSnapshot snap)
    {
        Theme.Caption("Akochan比較（助言のみ）");
        ImGui.Dummy(new Vector2(0, 3));

        if (plugin.Policy is not Mahjong.Plugin.Dalamud.ExternalAi.SelectablePolicy selectable)
        {
            Theme.Subtle("Akochan比較を利用できません。");
            return;
        }

        if (!selectable.TryGetAkochanChoice(snap, out ActionChoice akochanChoice))
        {
            Theme.Subtle(selectable.AkochanStatus);
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
        ImGui.TextUnformatted(FriendlyActionVerb(akochanChoice.Kind));
        ImGui.PopStyleColor();

        if (akochanChoice.DiscardTile is { } tile)
        {
            ImGui.SameLine(0, 10);
            Theme.DrawTile(tile, new Vector2(Theme.SmallTileW, Theme.SmallTileH));
            ImGui.SameLine(0, 10);
            ImGui.TextUnformatted(FriendlyTileName(tile));
        }

        if (akochanChoice.Call is { } call)
            DrawCallPatternInstruction(snap, akochanChoice, call, compact: true);

        Theme.Subtle($"Akochan · {selectable.AkochanInferenceMs} ms · never dispatched");
    }

    private void DrawAkochanStatus()
    {
        Theme.Caption("Akochan状態（助言のみ）");
        ImGui.Dummy(new Vector2(0, 3));

        if (plugin.Policy is not Mahjong.Plugin.Dalamud.ExternalAi.SelectablePolicy selectable)
        {
            Theme.Subtle("利用不可");
            return;
        }

        Theme.Subtle($"Status: {selectable.AkochanStatus}");
        Theme.Subtle($"Last inference: {selectable.AkochanInferenceMs} ms · Restarts: {selectable.AkochanRestartCount} · never dispatched");
    }

    private void DrawEmptyLive()
    {
        var cfg = plugin.Configuration;
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
        ImGui.TextWrapped("ドマ式麻雀の対局開始を待っています。");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.TextWrapped("対局が始まると、次の動作を行います。");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 2));

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
        ImGui.BulletText("ヒント — 麻雀画面上で最善の打牌を枠線表示します。");
        ImGui.PopStyleColor();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        ImGui.BulletText("自動プレイ — 人間らしい間隔で最善の打牌を自動選択します。");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));
        string modeHint = !cfg.AutomationArmed
            ? "現在は停止中です。上の「ヒント」または「自動プレイ」を選択してください。"
            : cfg.SuggestionOnly
                ? "ヒントモードが有効です。対局を開始すると候補牌が枠線表示されます。"
                : "自動プレイが有効です。対局を開始すると自動操作を行います。";
        Theme.Subtle(modeHint);
    }

    private void DrawSeatRow(StateSnapshot snap)
    {
        string[] labels = { "自家", "下家", "対面", "上家" };
        float avail = ImGui.GetContentRegionAvail().X;
        float gap = 6f;
        float pillW = (avail - gap * 3) / 4f;
        for (int i = 0; i < 4; i++)
        {
            DrawSeatPill(labels[i], snap.Scores[i], isYou: i == 0, new Vector2(pillW, 40));
            if (i < 3)
                ImGui.SameLine(0, gap);
        }
    }

    private static void DrawSeatPill(string label, int score, bool isYou, Vector2 size)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        Vector4 tint = isYou ? Theme.Accent : Theme.Muted;
        Vector4 bg = Theme.Fade(tint, isYou ? 0.18f : 0.08f);

        dl.AddRectFilled(min, max, Theme.Pack(bg), 6f);
        dl.AddRect(min, max, Theme.Pack(tint, isYou ? 0.85f : 0.45f), 6f, ImDrawFlags.None, 1f);

        var labelSize = ImGui.CalcTextSize(label);
        var labelPos = min + new Vector2((size.X - labelSize.X) * 0.5f, 5);
        dl.AddText(labelPos, Theme.Pack(tint, 0.8f), label);

        string scoreStr = score.ToString();
        var scoreSize = ImGui.CalcTextSize(scoreStr);
        var scorePos = min + new Vector2((size.X - scoreSize.X) * 0.5f, size.Y - scoreSize.Y - 5);
        Vector4 scoreColor = isYou ? Theme.Header : Theme.Body;
        dl.AddText(scorePos, Theme.Pack(scoreColor), scoreStr);

        ImGui.Dummy(size);
    }

    private static void DrawHandRow(StateSnapshot snap, int highlightSlot)
    {
        Theme.Caption($"Hand · {snap.Hand.Count} tiles");
        ImGui.Dummy(new Vector2(0, 3));
        Theme.DrawHand(snap.Hand, highlightSlot);
    }

    private void DrawSuggestion(
        StateSnapshot snap,
        ScoredDiscard[]? scored,
        ActionChoice? choice,
        string? scorerError)
    {
        var cfg = plugin.Configuration;

        Theme.Caption($"Best move — {SelectedAiLabel(cfg.AiProvider)}");
        ImGui.Dummy(new Vector2(0, 3));

        if (choice is null)
        {
            Theme.Subtle($"Waiting for a decision ({snap.Hand.Count} tiles in hand, legal: {snap.Legal.Flags}).");
            return;
        }

        // Keep the pending marker in StateAggregator so it polls the external
        // engine again when the background task completes, but never render
        // that Pass-shaped internal marker as player advice.
        if (Mahjong.Plugin.Dalamud.ExternalAi.SelectablePolicy.IsPendingChoice(choice))
        {
            Theme.Subtle($"Calculating best move ({snap.Hand.Count} tiles in hand, legal: {snap.Legal.Flags}).");
            return;
        }

        // Discard scoring is optional for non-discard actions. Mortal can return
        // Pass, Riichi, Chi, Pon, Kan, Ron or Tsumo without a ScoredDiscard list.
        if (scorerError != null && choice.Kind == ActionKind.Discard)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Danger);
            ImGui.TextWrapped($"scorer error: {scorerError}");
            ImGui.PopStyleColor();
            return;
        }

        scored ??= [];

        string aiLabel = SelectedAiLabel(cfg.AiProvider);
        string[] declined = DeclinedLegalActions(snap, choice);
        // A bare Pass is ambiguous: it does not tell the player which offered
        // calls the AI declined.  Preserve that decision in the primary line
        // as well as the independent rows below it.
        string verb = choice.Kind == ActionKind.Pass && declined.Length > 0
            ? $"Pass ({string.Join(" / ", declined.Select(static action => $"No {action}"))})"
            : FriendlyActionVerb(choice.Kind);

        float startY = ImGui.GetCursorPosY();
        float bigH = Theme.BigTileH;
        float textH = ImGui.CalcTextSize("X").Y;
        float textY = startY + (bigH - textH) * 0.5f;

        ImGui.SetCursorPosY(textY);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        ImGui.TextUnformatted(verb);
        ImGui.PopStyleColor();

        if (choice.DiscardTile is { } t)
        {
            ImGui.SameLine(0, 12);
            ImGui.SetCursorPosY(startY);
            Theme.DrawTile(t, new Vector2(Theme.BigTileW, Theme.BigTileH), Theme.Pulse(1.4f, 0.55f, 1.0f));

            ImGui.SameLine(0, 12);
            ImGui.SetCursorPosY(textY);
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.TextUnformatted(FriendlyTileName(t));
            ImGui.PopStyleColor();
        }

        if (choice.DiscardTile is not null)
            ImGui.SetCursorPosY(startY + bigH + 4);

        if (choice.Call is { } call)
        {
            ImGui.Dummy(new Vector2(0, 3));
            DrawCallPatternInstruction(snap, choice, call, compact: false);
        }

        ImGui.Dummy(new Vector2(0, 3));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.TextWrapped($"Legal actions: {snap.Legal.Flags}");
        ImGui.PopStyleColor();

        DrawIndependentDecisionAxes(snap, choice);

        string why = declined.Length > 0
            ? $"{aiLabel} deliberately declines {string.Join(", ", declined)} and recommends {FriendlyActionVerb(choice.Kind).ToLowerInvariant()}."
            : ExplainChoice(choice, scored);
        if (!string.IsNullOrEmpty(why))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
            ImGui.TextWrapped(why);
            ImGui.PopStyleColor();
        }

        if (cfg.ShowInGameHighlight)
        {
            Theme.Subtle("対象牌を麻雀画面上で枠線表示しています。");
        }

        if (cfg.ShowSuggestionDetails)
        {
            ImGui.Dummy(new Vector2(0, 6));
            Theme.Subtle(
                "シャンテン数はテンパイまでに必要な手数です。受け入れは手を進める牌の種類数と、" +
                "山に残っている推定枚数を示します。");
            ImGui.Dummy(new Vector2(0, 4));
            int show = Math.Min(3, scored.Length);
            for (int i = 0; i < show; i++)
                DrawScoredPickRow(i, scored[i]);
        }

        if (plugin.AutoPlay.LastActionDescription != "なし")
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.TextWrapped($"Last action: {plugin.AutoPlay.LastActionDescription}");
            ImGui.PopStyleColor();
        }
    }

    private void DrawCallPatternInstruction(
        StateSnapshot snap,
        ActionChoice choice,
        MeldCandidate call,
        bool compact)
    {
        IReadOnlyList<MeldCandidate> candidates = call.Kind switch
        {
            MeldKind.Pon => snap.Legal.PonCandidates,
            MeldKind.Chi => snap.Legal.ChiCandidates,
            _ => snap.Legal.KanCandidates.Where(candidate => candidate.Kind == call.Kind).ToArray(),
        };

        MeldCandidate[] sameOffer = candidates
            .Where(candidate => candidate.Kind == call.Kind)
            .Where(candidate => candidate.ClaimedTile == call.ClaimedTile)
            .ToArray();
        int selectedIndex = Array.FindIndex(sameOffer, candidate => SameCallPattern(candidate, call));

        string[] consumed = call.HandTiles
            .Select((tile, index) => FriendlyCallTileName(
                tile,
                index < choice.CallConsumedRed.Count && choice.CallConsumedRed[index]))
            .ToArray();
        string handPattern = consumed.Length == 0 ? "なし" : string.Join(" + ", consumed);
        string claimed = FriendlyTileName(call.ClaimedTile);
        string result = string.Join(" ", call.HandTiles
            .Append(call.ClaimedTile)
            .OrderBy(tile => tile.Id)
            .Select(FriendlyTileName));

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        ImGui.TextWrapped($"Call pattern: use {handPattern}; claim {claimed}");
        ImGui.PopStyleColor();

        if (!compact)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.TextWrapped($"Meld result: {result}");
            ImGui.PopStyleColor();
        }

        if (sameOffer.Length > 1)
        {
            string position = selectedIndex >= 0 ? $"{selectedIndex + 1}/{sameOffer.Length}" : $"1/{sameOffer.Length}";
            bool automatic = Mahjong.Plugin.Dalamud.Actions.AutoPlayLoop.IsAutomaticCallAllowed(
                plugin.Configuration,
                choice.Kind);
            string behavior = automatic
                ? "この組み合わせを自動選択します"
                : "この組み合わせを手動で選択してください";
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
            ImGui.TextWrapped($"Pattern selection: {position} — {behavior}.");
            ImGui.PopStyleColor();
        }
    }

    private static bool SameCallPattern(MeldCandidate left, MeldCandidate right)
    {
        if (left.Kind != right.Kind || left.ClaimedTile != right.ClaimedTile)
            return false;
        return left.HandTiles.Select(tile => tile.Id).OrderBy(id => id)
            .SequenceEqual(right.HandTiles.Select(tile => tile.Id).OrderBy(id => id));
    }

    private static string FriendlyCallTileName(Tile tile, bool isRed) =>
        isRed ? $"Red {FriendlyTileName(tile)}" : FriendlyTileName(tile);

    private static void DrawScoredPickRow(int rank, ScoredDiscard s)
    {
        float rowStart = ImGui.GetCursorPosY();
        float tileH = Theme.SmallTileH;
        float textH = ImGui.CalcTextSize("X").Y;
        float textY = rowStart + (tileH - textH) * 0.5f;

        ImGui.SetCursorPosY(textY);
        Vector4 rankColor = rank == 0 ? Theme.Accent : Theme.Muted;
        ImGui.PushStyleColor(ImGuiCol.Text, rankColor);
        ImGui.TextUnformatted($"{rank + 1}.");
        ImGui.PopStyleColor();

        ImGui.SameLine(0, 8);
        ImGui.SetCursorPosY(rowStart);
        Theme.DrawTile(s.Discard, new Vector2(Theme.SmallTileW, Theme.SmallTileH));

        ImGui.SameLine(0, 10);
        ImGui.SetCursorPosY(textY);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
        ImGui.TextUnformatted($"shanten {s.ShantenAfter}    ukeire {s.UkeireKinds} kinds · {s.UkeireWeighted} tiles");
        ImGui.PopStyleColor();

        ImGui.SetCursorPosY(rowStart + tileH + 3);
    }

    private static string FriendlyActionVerb(ActionKind kind) => kind switch
    {
        ActionKind.Discard => "打牌",
        ActionKind.Riichi => "リーチ",
        ActionKind.Tsumo => "ツモ和了",
        ActionKind.Ron => "ロン和了",
        ActionKind.Pon => "ポン",
        ActionKind.Chi => "チー",
        ActionKind.AnKan => "暗槓",
        ActionKind.MinKan => "大明槓",
        ActionKind.ShouMinKan => "加槓",
        ActionKind.Pass => "パス",
        _ => kind.ToString(),
    };

    private static string[] DeclinedLegalActions(StateSnapshot snap, ActionChoice choice)
    {
        var declined = new List<string>(8);

        AddIfDeclined(ActionFlags.Discard, "打牌", ActionKind.Discard);
        AddIfDeclined(ActionFlags.Riichi, "リーチ", ActionKind.Riichi);
        AddIfDeclined(ActionFlags.Tsumo, "ツモ", ActionKind.Tsumo);
        AddIfDeclined(ActionFlags.Ron, "ロン", ActionKind.Ron);
        AddIfDeclined(ActionFlags.Pon, "ポン", ActionKind.Pon);
        AddIfDeclined(ActionFlags.Chi, "チー", ActionKind.Chi);
        if (snap.Legal.Can(ActionFlags.AnKan)
            || snap.Legal.Can(ActionFlags.MinKan)
            || snap.Legal.Can(ActionFlags.ShouMinKan))
        {
            bool choseKan = choice.Kind is ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan;
            if (!choseKan)
                declined.Add("カン");
        }

        return [.. declined];

        void AddIfDeclined(ActionFlags flag, string name, ActionKind matchingKind)
        {
            if (snap.Legal.Can(flag) && choice.Kind != matchingKind)
                declined.Add(name);
        }
    }

    private static void DrawIndependentDecisionAxes(StateSnapshot snap, ActionChoice choice)
    {
        // Show every offer on its own line.  This is intentionally based only
        // on LegalActions, so stale or unavailable UI offers are never shown
        // as a decision that the player could make.
        DrawAxis(ActionFlags.Riichi, "リーチ", ActionKind.Riichi);
        DrawAxis(ActionFlags.Tsumo, "ツモ", ActionKind.Tsumo);
        DrawAxis(ActionFlags.Ron, "ロン", ActionKind.Ron);
        DrawAxis(ActionFlags.Pon, "ポン", ActionKind.Pon);
        DrawAxis(ActionFlags.Chi, "チー", ActionKind.Chi);

        if (snap.Legal.Can(ActionFlags.AnKan)
            || snap.Legal.Can(ActionFlags.MinKan)
            || snap.Legal.Can(ActionFlags.ShouMinKan))
        {
            bool chooseKan = choice.Kind is ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan;
            DrawDecisionLine("カン", chooseKan);
        }

        return;

        void DrawAxis(ActionFlags flag, string label, ActionKind matchingKind)
        {
            if (snap.Legal.Can(flag))
                DrawDecisionLine(label, choice.Kind == matchingKind);
        }
    }

    private static void DrawDecisionLine(string label, bool selected)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? Theme.Accent : Theme.Muted);
        ImGui.TextUnformatted($"{label}: {(selected ? "Choose" : "No")}");
        ImGui.PopStyleColor();
    }

    private static string SelectedAiLabel(AiProvider provider) => provider switch
    {
        AiProvider.BundledMortal => "Mortal 298k",
        AiProvider.BundledAkochan => "Akochan",
        AiProvider.ExternalMjai => "外部mjai",
        _ => "内蔵AI",
    };

    private static string FriendlyTileName(Tile tile) => Theme.TileFriendlyName(tile);

    private static string ExplainChoice(ActionChoice choice, ScoredDiscard[] scored)
    {
        switch (choice.Kind)
        {
            case ActionKind.Tsumo:
            case ActionKind.Ron:
                return "和了可能です。和了を選択します。";
            case ActionKind.Riichi:
                return "テンパイしています。リーチを選択します。";
            case ActionKind.Pon:
            case ActionKind.Chi:
            case ActionKind.AnKan:
            case ActionKind.MinKan:
            case ActionKind.ShouMinKan:
                return "鳴くことで面子を完成させます。";
        }

        if (scored.Length == 0) return "";
        var top = scored[0];
        string kindNoun = top.UkeireKinds == 1 ? "種" : "種";
        string tileNoun = top.UkeireWeighted == 1 ? "枚" : "tiles";

        if (top.ShantenAfter < 0)
            return "この牌を切っても和了形を維持できます。";
        if (top.ShantenAfter == 0)
            return $"Keeps you ready — {top.UkeireKinds} {kindNoun} ({top.UkeireWeighted} {tileNoun} live) complete the hand.";
        if (top.ShantenAfter == 1)
            return $"One step from ready, with {top.UkeireKinds} useful {kindNoun} to draw.";
        return $"{top.ShantenAfter} steps from ready — keeps the most useful draws available.";
    }
}
