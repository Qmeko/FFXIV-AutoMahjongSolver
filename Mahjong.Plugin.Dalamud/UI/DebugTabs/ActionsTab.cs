using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.UI.DebugTabs;

/// <summary>Manual dispatch surface: auto-discard, test slot, test call option.</summary>
internal sealed class ActionsTab
{
    private readonly DevConsoleContext ctx;
    private int testDiscardSlot = 13;
    private int testCallOption;

    public ActionsTab(DevConsoleContext ctx) => this.ctx = ctx;

    public void Draw()
    {
        var snap = ctx.Plugin.Aggregator.Latest;
        bool addonPresent = snap is not null;
        bool ourTurn = addonPresent && snap!.Legal.Can(ActionFlags.Discard);

        using (Theme.BeginCard("actions-auto"))
        {
            Theme.SectionHeader("自動打牌");
            Theme.Subtle("AIに打牌を問い合わせ、クリック操作を実行します。自分の手番でのみ動作します。");
            using (DevHelpers.Disable(!ourTurn))
            {
                float w = ImGui.GetContentRegionAvail().X;
                if (ImGui.Button("AI選択を実行", new Vector2(w, 34)))
                    ctx.Framework.RunOnFrameworkThread(TriggerAutoDiscard);
            }
            if (!ourTurn)
                Theme.Subtle(addonPresent ? "自分の手番ではありません。" : "スナップショットがありません。先に対局を開始してください。");
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("actions-testslot"))
        {
            Theme.SectionHeader("打牌スロットをテスト");
            Theme.Subtle("AI判断を無視して指定手牌スロットを打牌します。0は左端、13はツモ牌です。");

            ImGui.SetNextItemWidth(90);
            ImGui.InputInt("##slot", ref testDiscardSlot);
            testDiscardSlot = Math.Clamp(testDiscardSlot, 0, 13);
            ImGui.SameLine(0, 8);
            using (DevHelpers.Disable(!ourTurn))
            {
                if (ImGui.Button($"Dispatch slot {testDiscardSlot}"))
                {
                    int slot = testDiscardSlot;
                    ctx.Framework.RunOnFrameworkThread(() =>
                    {
                        var r = ctx.Plugin.Dispatcher.DispatchDiscard(slot);
                        ctx.LastToast = $"discard slot={slot} → {r}";
                    });
                }
            }

            ImGui.Dummy(new Vector2(0, 3));
            if (ImGui.SmallButton("ツモ牌（13）"))
                testDiscardSlot = 13;
            ImGui.SameLine(0, 4);
            if (ImGui.SmallButton("左端（0）"))
                testDiscardSlot = 0;
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("actions-testcall"))
        {
            Theme.SectionHeader("鳴き選択肢をテスト");
            Theme.Subtle("鳴き確認のボタン番号を直接送信します。各番号の対応先は診断のイベントログを有効にして確認してください。");

            ImGui.SetNextItemWidth(90);
            ImGui.InputInt("##opt", ref testCallOption);
            testCallOption = Math.Clamp(testCallOption, 0, 5);
            ImGui.SameLine(0, 8);
            if (ImGui.Button($"Dispatch opt {testCallOption}"))
            {
                int opt = testCallOption;
                ctx.Framework.RunOnFrameworkThread(() =>
                {
                    var r = ctx.Plugin.Dispatcher.DispatchCallOption(opt);
                    ctx.LastToast = $"call opt={opt} → {r}";
                });
            }

            ImGui.Dummy(new Vector2(0, 3));
            for (int i = 0; i < 3; i++)
            {
                if (i > 0)
                    ImGui.SameLine(0, 4);
                int v = i;
                if (ImGui.SmallButton(v.ToString()))
                    testCallOption = v;
            }

            ImGui.Dummy(new Vector2(0, 3));
            Theme.Subtle("ポン／パス: 0=パス、1=ポン。チー／パス: 推定0=チー、1=パス。ログで確認してください。");
        }
    }

    private void TriggerAutoDiscard()
    {
        var snap = ctx.Plugin.AddonReader.TryBuildSnapshot();
        if (snap is null || !snap.Legal.Can(ActionFlags.Discard))
        {
            ctx.LastToast = "auto-discard: not our turn";
            return;
        }

        var choice = ctx.Plugin.Policy.Choose(snap);
        if (choice.Kind != ActionKind.Discard || choice.DiscardTile is null)
        {
            ctx.LastToast = $"auto-discard: policy returned {choice.Kind}";
            return;
        }

        var tile = choice.DiscardTile.Value;
        int slot = ctx.Plugin.AddonReader.FindAddonSlotOfTile(tile);
        if (slot < 0)
        {
            ctx.LastToast = $"auto-discard: tile {tile} not found in hand";
            return;
        }

        var delay = HumanTiming.RandomDelay();
        ctx.LastToast = $"auto-discarding {tile} slot {slot} in {delay.TotalMilliseconds:F0}ms";

        _ = ctx.Framework.RunOnTick(() =>
        {
            var r = ctx.Plugin.Dispatcher.DispatchDiscard(slot);
            ctx.LastToast = $"auto-discarded {tile} → {r}";
        }, delay);
    }
}
