using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Mahjong.Plugin.Dalamud.UI.DebugTabs;

/// <summary>RE primitives: walknodes, findtiles, poolslots/icons, hexdump, dumpmem, atkvalues, addons.</summary>
internal sealed class ReProbesTab
{
    private readonly DevConsoleContext ctx;
    private string hexStart = "0x0C00";
    private string hexEnd = "0x1400";
    private bool hexAgent;
    private string memOffset = "0x0238";
    private string memLength = "0x0400";
    private string addonsFilter = "";

    public ReProbesTab(DevConsoleContext ctx) => this.ctx = ctx;

    public void Draw()
    {
        var cmd = ctx.Plugin.MjAutoCommand;

        using (Theme.BeginCard("re-tree"))
        {
            Theme.SectionHeader("アドオンツリーと構造解析");
            Theme.Subtle("walknodesは全UIノード、findtilesは牌値候補、atkvaluesはアドオン引数配列を調査します。");
            if (ImGui.Button("ノード一覧を出力"))
            {
                cmd.HandleWalkNodes();
                ctx.LastToast = "walknodes queued → emj-nodes.txt";
            }
            ImGui.SameLine(0, 3);
            DevHelpers.CopyPathButton("emj-nodes.txt", "nodes");
            ImGui.SameLine(0, 10);
            if (ImGui.Button("牌候補を検索"))
            {
                cmd.HandleFindTiles();
                ctx.LastToast = "findtiles queued → emj-findtiles.txt";
            }
            ImGui.SameLine(0, 3);
            DevHelpers.CopyPathButton("emj-findtiles.txt", "findtiles");
            ImGui.SameLine(0, 10);
            if (ImGui.Button("AtkValueを出力"))
            {
                cmd.DumpAtkValues();
                ctx.LastToast = "atkvalues → emj-atkvalues.txt";
            }
            ImGui.SameLine(0, 3);
            DevHelpers.CopyPathButton("emj-atkvalues.txt", "atkvalues");
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("re-pool"))
        {
            Theme.SectionHeader("打牌プールのスロット差分");
            Theme.Subtle("異なる牌のスロットを比較し、各プール牌スロット内で牌IDを表す項目を特定します。");
            if (ImGui.Button("プールスロットを出力"))
            {
                cmd.HandlePoolSlots();
                ctx.LastToast = "poolslots queued → emj-poolslots.txt";
            }
            ImGui.SameLine(0, 3);
            DevHelpers.CopyPathButton("emj-poolslots.txt", "poolslots");
            ImGui.SameLine(0, 10);
            if (ImGui.Button("プールアイコンを出力"))
            {
                cmd.HandlePoolIcons();
                ctx.LastToast = "poolicons queued → emj-poolicons.txt";
            }
            ImGui.SameLine(0, 3);
            DevHelpers.CopyPathButton("emj-poolicons.txt", "poolicons");
            Theme.Subtle("型1021～1024を走査し、表示中スロット間の牌候補値を比較します。");
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("re-hex"))
        {
            Theme.SectionHeader("16進表示");
            Theme.Subtle("アドオンまたはAgentEmjの指定範囲を16進形式で表示します。");
            ImGui.Checkbox("Agentを対象にする", ref hexAgent);
            ImGui.SetNextItemWidth(120);
            ImGui.InputText("start##hex", ref hexStart, 16);
            ImGui.SameLine(0, 8);
            ImGui.SetNextItemWidth(120);
            ImGui.InputText("end##hex", ref hexEnd, 16);

            bool startOk = DevHelpers.TryParseHex(hexStart, out _);
            bool endOk = DevHelpers.TryParseHex(hexEnd, out _);
            using (DevHelpers.Disable(!(startOk && endOk)))
            {
                if (ImGui.Button("16進情報を出力"))
                {
                    var args = (hexAgent ? "agent " : "") + $"{hexStart} {hexEnd}";
                    cmd.HandleHexDump(args);
                    ctx.LastToast = $"hexdump {args.Trim()} → emj-hexdump{(hexAgent ? "-agent" : "")}.txt";
                }
            }
            ImGui.SameLine(0, 6);
            DevHelpers.CopyPathButton(hexAgent ? "emj-hexdump-agent.txt" : "emj-hexdump.txt", "hex");
            if (!startOk || !endOk)
                Theme.Subtle("16進数を使用してください。0x接頭辞は省略できます。");
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("re-mem"))
        {
            Theme.SectionHeader("アドオン情報（開始位置＋長さ）");
            Theme.Subtle("アドオンのみを対象に指定範囲をemj-dump.txtへ保存します。");
            ImGui.SetNextItemWidth(120);
            ImGui.InputText("offset##mem", ref memOffset, 16);
            ImGui.SameLine(0, 8);
            ImGui.SetNextItemWidth(120);
            ImGui.InputText("length##mem", ref memLength, 16);

            bool offOk = DevHelpers.TryParseHex(memOffset, out _);
            bool lenOk = DevHelpers.TryParseHex(memLength, out _);
            using (DevHelpers.Disable(!(offOk && lenOk)))
            {
                if (ImGui.Button("診断情報を出力"))
                {
                    cmd.DumpMemory($"{memOffset} {memLength}");
                    ctx.LastToast = $"dumpmem +{memOffset} len {memLength} → emj-dump.txt";
                }
            }
            ImGui.SameLine(0, 6);
            DevHelpers.CopyPathButton("emj-dump.txt", "dump");
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("re-addons"))
        {
            Theme.SectionHeader("読込済みアドオン一覧");
            Theme.Subtle("現在読み込まれているゲームUIアドオンを一覧表示します。");
            ImGui.SetNextItemWidth(220);
            ImGui.InputText("filter##addons", ref addonsFilter, 64);
            ImGui.SameLine(0, 8);
            if (ImGui.Button("アドオン一覧を表示"))
            {
                cmd.DumpAddons(addonsFilter);
                ctx.LastToast = "addons → chat";
            }
        }
    }
}
