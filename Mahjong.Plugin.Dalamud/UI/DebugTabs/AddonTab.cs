using System.Numerics;
using Dalamud.Bindings.ImGui;
using Mahjong.Plugin.Dalamud.GameState;

namespace Mahjong.Plugin.Dalamud.UI.DebugTabs;

/// <summary>AddonEmj lifecycle, plus the active variant and layout profile.</summary>
internal sealed class AddonTab
{
    private readonly DevConsoleContext ctx;

    public AddonTab(DevConsoleContext ctx) => this.ctx = ctx;

    public void Draw()
    {
        using (Theme.BeginCard("addon"))
        {
            Theme.SectionHeader("AddonEmj");
            Theme.Subtle("麻雀UIのアドレスと表示状態です。卓に着くまでは空欄になります。");
            var obs = ctx.Plugin.AddonReader.Poll();
            if (!obs.Present)
            {
                var candidates = string.Join("\" or \"", MahjongAddon.CandidateNames);
                Theme.Subtle($"Addon \"{candidates}\" not found. Open a Doman Mahjong match in-game.");
                if (obs.LastLifecycleEvent is not null)
                {
                    ImGui.Dummy(new Vector2(0, 4));
                    DevHelpers.KeyValueRow("最終イベント", obs.LastLifecycleEvent);
                }
                return;
            }

            DevHelpers.KeyValueRow("アドレス", $"0x{obs.Address:X}");
            DevHelpers.KeyValueRow("表示中", obs.IsVisible.ToString());
            DevHelpers.KeyValueRow("最終イベント", obs.LastLifecycleEvent ?? "(none)");
        }

        ImGui.Dummy(new Vector2(0, 4));
        DrawVariantCard();
        ImGui.Dummy(new Vector2(0, 4));
        DrawLayoutCard();
    }

    private void DrawVariantCard()
    {
        using (Theme.BeginCard("addon-variants"))
        {
            Theme.SectionHeader("バリアント判定");
            Theme.Subtle("現在のアドオンに一致した読取方式を表示します。すべて不一致の場合は「不具合報告→バリアントを出力」を実行してください。");

            var selector = ctx.Plugin.AddonReader.Selector;
            if (selector.Variants.Count == 0)
            {
                Theme.Subtle("登録されたバリアントなし");
                return;
            }

            foreach (var v in selector.Variants)
            {
                string status = ProbeStatus(v);
                DevHelpers.KeyValueRow(v.Name, status);
            }
        }
    }

    private unsafe string ProbeStatus(GameState.Variants.IEmjVariant v)
    {
        if (!ctx.Addon.TryGet(out var unit, out _))
            return "アドオンなし";
        try
        {
            return v.Probe(unit) ? "一致" : "不一致";
        }
        catch (System.Exception ex)
        {
            return $"threw: {ex.GetType().Name}";
        }
    }

    private void DrawLayoutCard()
    {
        using (Theme.BeginCard("addon-layout"))
        {
            Theme.SectionHeader("有効なレイアウトプロファイル");
            Theme.Subtle("手牌、点数、ドラ表示牌を取得するために使用中の読取位置です。");

            var layout = ctx.Plugin.AddonReader.ActiveLayout;
            if (layout is null)
            {
                Theme.Subtle("有効なプロファイルなし — アドオンはまだ解析されていません");
                return;
            }

            DevHelpers.KeyValueRow("名前", layout.Name);
            DevHelpers.KeyValueRow("アドオン名", layout.AddonName);
            DevHelpers.KeyValueRow("牌テクスチャ基準値", layout.TileTextureBase.ToString());
            DevHelpers.KeyValueRow("手牌配列開始位置", $"0x{layout.Offsets.HandArrayStart:X}");
            DevHelpers.KeyValueRow("自家点数", $"0x{layout.Offsets.SelfScore:X}");
            DevHelpers.KeyValueRow("ドラ表示牌", $"0x{layout.Offsets.DoraIndicator:X}");
            DevHelpers.KeyValueRow("手牌枚数上限", layout.Limits.HandSize.ToString());
        }
    }
}
