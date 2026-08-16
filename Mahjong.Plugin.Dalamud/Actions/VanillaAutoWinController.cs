using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.Dalamud.GameState;

namespace Mahjong.Plugin.Dalamud.Actions;

/// <summary>
/// Enables the game's own 「自動あがり」 toggle when plugin auto-play is selected.
/// The operation is limited to the visible Emj/EmjL addon and uses the public
/// addon node tree plus ReceiveEvent; it does not read or write raw process memory.
/// </summary>
public sealed class VanillaAutoWinController
{
    private static readonly string[] AutoWinLabels =
    {
        "自動あがり",
        "自動上がり",
        "Auto Win",
    };

    private readonly MahjongAddon addon;
    private readonly IPluginLog log;

    public VanillaAutoWinController(MahjongAddon addon, IPluginLog log)
    {
        this.addon = addon;
        this.log = log;
    }

    /// <summary>Turns the vanilla option on. It never toggles the option off.</summary>
    public unsafe bool Enable()
    {
        if (!addon.TryGet(out var unit, out var addonName) || !unit->IsVisible)
        {
            log.Info("[VanillaAutoWin] 自動プレイを有効化したが、麻雀画面が表示されていないため自動あがり設定は変更しませんでした。");
            return false;
        }

        var manager = &unit->UldManager;
        if (manager->NodeList == null || manager->NodeListCount == 0)
        {
            log.Warning("[VanillaAutoWin] 麻雀画面のノード一覧を取得できませんでした。");
            return false;
        }

        for (int i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || (int)node->Type < 1000 || !node->IsVisible())
                continue;

            var componentNode = (AtkComponentNode*)node;
            var component = componentNode->Component;
            if (component == null)
                continue;

            if (!ComponentContainsAutoWinLabel(component))
                continue;

            // ON is rendered in the same component as the label. Never click an
            // already enabled toggle because this control is a true toggle.
            if (ComponentShowsEnabled(component))
            {
                log.Info("[VanillaAutoWin] バニラの自動あがりは既にONです。 addon={Addon}", addonName);
                return true;
            }

            var collision = FindClickableCollision(component);
            if (collision == null)
            {
                log.Warning("[VanillaAutoWin] 自動あがり項目は見つかりましたが、クリック対象を取得できませんでした。");
                return false;
            }

            var atkEvent = new AtkEvent
            {
                Listener = (AtkEventListener*)component,
                Node = collision,
                Target = (AtkEventTarget*)collision,
            };

            unit->ReceiveEvent(AtkEventType.ButtonClick, 0, &atkEvent);
            log.Info("[VanillaAutoWin] 自動プレイ選択に連動してバニラの自動あがりをONにしました。 addon={Addon} node={NodeId}", addonName, node->NodeId);
            return true;
        }

        log.Warning("[VanillaAutoWin] 表示中の麻雀画面から自動あがり設定を見つけられませんでした。");
        return false;
    }

    private static unsafe bool ComponentContainsAutoWinLabel(AtkComponentBase* component)
    {
        var manager = &component->UldManager;
        if (manager->NodeList == null)
            return false;

        for (int i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || node->Type != NodeType.Text)
                continue;

            var text = ((AtkTextNode*)node)->NodeText.ToString();
            foreach (var label in AutoWinLabels)
                if (text.Contains(label, StringComparison.OrdinalIgnoreCase))
                    return true;
        }

        return false;
    }

    private static unsafe bool ComponentShowsEnabled(AtkComponentBase* component)
    {
        var manager = &component->UldManager;
        if (manager->NodeList == null)
            return false;

        for (int i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || node->Type != NodeType.Text || !node->IsVisible())
                continue;

            string text = ((AtkTextNode*)node)->NodeText.ToString().Trim();
            if (text.Equals("ON", StringComparison.OrdinalIgnoreCase)
                || text.Equals("オン", StringComparison.OrdinalIgnoreCase)
                || text.Equals("有効", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static unsafe AtkResNode* FindClickableCollision(AtkComponentBase* component)
    {
        var manager = &component->UldManager;
        if (manager->NodeList == null)
            return null;

        for (int i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node != null && node->Type == NodeType.Collision && node->IsVisible())
                return node;
        }

        return null;
    }
}
