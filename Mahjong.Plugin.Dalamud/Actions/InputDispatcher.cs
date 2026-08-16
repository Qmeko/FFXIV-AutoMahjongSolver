using System;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Game.Variants;

namespace Mahjong.Plugin.Dalamud.Actions;

/// <summary>
/// Sends input events to the <c>Emj</c> addon via <c>AtkUnitBase.FireCallback</c>.
/// All calls must be made from the framework thread.
///
/// Callback patterns discovered during M6 logging (see <c>memory/project_addon_emj_re_notes.md</c>):
/// <list type="bullet">
///   <item><description>Discard tile at slot N (0-13): <c>FireCallback([Int=7, Int=N])</c></description></item>
///   <item><description>Pass on a call prompt:      <c>FireCallback([Int=11, Int=0])</c></description></item>
/// </list>
/// Pon/Chi/Kan/Riichi/Tsumo/Ron patterns are still unmapped — need a logging session
/// where the user actually triggers those actions.
/// </summary>
public sealed class InputDispatcher
{
    // Fallback values match data/layouts/{emj,emj_l}.json. Used when the layout accessor isn't wired (tests) or hasn't resolved yet (no addon attached).
    private const int DefaultSelfDeclareListCode = 6;
    private const int DefaultCallPromptCode = 15;
    private const int DefaultCallPromptListCode = 28;
    private const int DefaultOurTurnDiscardCode = 30;
    private const int DefaultHandArrayStartOffset = 0x0DB8;

    private readonly MahjongAddon addon;
    private readonly Func<LayoutProfile?>? layoutAccessor;
    private readonly ExecutionTraceLog? trace;

    public InputDispatcher(MahjongAddon addon, Func<LayoutProfile?>? layoutAccessor = null, ExecutionTraceLog? trace = null)
    {
        ArgumentNullException.ThrowIfNull(addon);
        this.addon = addon;
        this.layoutAccessor = layoutAccessor;
        this.trace = trace;
    }

    private int SelfDeclareListCode =>
        layoutAccessor?.Invoke()?.StateCodes.SelfDeclareList ?? DefaultSelfDeclareListCode;
    private int CallPromptCode =>
        layoutAccessor?.Invoke()?.StateCodes.CallPrompt ?? DefaultCallPromptCode;
    private int CallPromptListCode =>
        layoutAccessor?.Invoke()?.StateCodes.CallPromptList ?? DefaultCallPromptListCode;
    private int OurTurnDiscardCode =>
        layoutAccessor?.Invoke()?.StateCodes.OurTurnDiscard ?? DefaultOurTurnDiscardCode;
    private int HandArrayStartOffset =>
        layoutAccessor?.Invoke()?.Offsets.HandArrayStart ?? DefaultHandArrayStartOffset;

    public enum DispatchResult
    {
        Ok,
        AddonNotFound,
        AddonNotVisible,
        InvalidSlot,
        HookFailed,         // FireCallback returned false (wrong state / invalid args)
    }

    /// <summary>
    /// Records which click path the most recent <see cref="DispatchDiscard"/> took
    /// (opcode-15 tile-click, list-widget SelectItem, opcode-7 slot-discard, or
    /// the bail reasons). The discard dispatcher has three viable paths and the
    /// uniform <c>DispatchResult.Ok</c> return value can't tell them apart —
    /// every shipped path returns Ok even when the game silently no-ops the
    /// click. AutoPlayLoop reads this to annotate <c>dispatch_attempted</c>
    /// findings so the next stall is unambiguous in the corpus.
    /// </summary>
    public string LastDiscardPath { get; private set; } = "(none)";

    /// <summary>
    /// Discard the tile at the given closed-hand slot (0..13). Slot 13 = last-drawn tile.
    ///
    /// <para><b>The discard protocol is a TWO-callback handshake</b> — verified by
    /// capturing a real manual user discard via the FireCallback hook on
    /// 2026-05-23:</para>
    ///
    /// <code>
    ///   11:15:11.659  FireCallback [15, textureId]  ← select tile (highlights + dismisses popup)
    ///   11:15:11.659  state transitions to 30 (if it wasn't already)
    ///   11:15:11.972  FireCallback [7,  slotIndex]  ← commit (discards the selected tile)
    ///   11:15:11.995  hand updates (tile gone)
    ///   </code>
    ///
    /// <para>Either call alone does <i>nothing</i> committed: opcode-15 only
    /// sets an internal "selected tile" marker and dismisses the self-declare
    /// popup, while opcode-7 only commits whatever was previously selected.
    /// The bot used to fire only one of them depending on state code, which
    /// is why dispatches reported <c>Ok</c> for months but tiles never left
    /// the user's hand — captured 2026-05-23 in the dev-build trial as 14:00
    /// onwards: <c>[15, raw]</c> at state=6, <c>[7, slot]</c> at state=30,
    /// neither committing because they were never paired.</para>
    ///
    /// <para>Both callbacks always fire — back-to-back, synchronously, no
    /// inter-call delay. State-6→30 transition happens inside opcode-15's
    /// internal handler, so opcode-7 sees the correct state by the time it
    /// runs. The 313 ms gap in the manual capture is just mouse-down vs.
    /// mouse-up timing, not a required interval.</para>
    ///
    /// <para><b>List-widget post-call branch</b> (<see cref="TryDispatchListItemClick"/>)
    /// covers state-6 with hand != 14 (post-pon/chi discard popup) and
    /// state-28 (CallPromptList novice-table popup). Those use the
    /// AtkComponentList SelectItem vfunc instead of the two-callback
    /// handshake — verified across post-chi/pon discard popups 2026-05-10..05-18.</para>
    /// </summary>
    public unsafe DispatchResult DispatchDiscard(int slotIndex)
    {
        trace?.Record("dispatcher.discard.enter", new Dictionary<string, object?> { ["slot"] = slotIndex });
        if (slotIndex is < 0 or > 13)
        {
            LastDiscardPath = "invalid-slot";
            return DispatchResult.InvalidSlot;
        }

        if (!addon.TryGet(out var unit, out _))
        {
            LastDiscardPath = "addon-not-found";
            return DispatchResult.AddonNotFound;
        }
        if (!unit->IsVisible)
        {
            LastDiscardPath = "addon-not-visible";
            return DispatchResult.AddonNotVisible;
        }

        int stateCode = ReadStateCode(unit);
        int handCount = ReadCurrentHandCount(unit);

        // Two-callback discard handshake — see method docstring for the
        // capture-verified protocol. Fires for any deal-shape closed hand
        // (count % 3 == 2 → 14, 11, 8, 5, 2 tiles depending on prior calls).
        //
        // EMJ can accept Chi/Pon and shrink the hand before it removes the old
        // state-15 call buttons.  That state is already the mandatory post-call
        // discard surface even though the visual modal still says Chi/Pon/Pass.
        // Treating state 15 as a call prompt at hand=11/8/5 left the window on
        // screen and prevented the selected AI discard from committing.  The
        // hand shape is the authoritative discriminator: an untouched opponent
        // call prompt has 13 concealed tiles and is therefore excluded.
        bool isTileClickDiscard = IsTileClickDiscardSurface(
            stateCode, handCount, SelfDeclareListCode, CallPromptCode, OurTurnDiscardCode);
        if (isTileClickDiscard)
        {
            int raw = ReadHandSlotRaw(unit, slotIndex);
            if (raw > 0)
            {
                nint selectUnitAddress = (nint)unit;
                var v15 = stackalloc AtkValue[2];
                v15[0].SetInt(15);
                v15[1].SetInt(raw);
                bool selectOk = unit->FireCallback(2, v15, true);

                // Opcode 15 can synchronously refresh/rebuild the Emj addon while
                // changing state 6 -> 30. Never invoke opcode 7 through the pointer
                // captured before that callback. Resolve the live addon again and
                // commit through its current AtkUnitBase instance.
                if (!addon.TryGet(out var commitUnit, out _))
                {
                    LastDiscardPath =
                        $"opcode-15-only(raw={raw},slot={slotIndex},selectOk={selectOk},reacquire=not-found)";
                    trace?.Record("dispatcher.discard.exit", new Dictionary<string, object?>
                    {
                        ["result"] = "HookFailed",
                        ["path"] = LastDiscardPath,
                        ["state"] = stateCode,
                        ["hand"] = handCount,
                        ["raw"] = raw,
                    });
                    return DispatchResult.HookFailed;
                }
                if (!commitUnit->IsVisible)
                {
                    LastDiscardPath =
                        $"opcode-15-only(raw={raw},slot={slotIndex},selectOk={selectOk},reacquire=not-visible)";
                    trace?.Record("dispatcher.discard.exit", new Dictionary<string, object?>
                    {
                        ["result"] = "HookFailed",
                        ["path"] = LastDiscardPath,
                        ["state"] = stateCode,
                        ["hand"] = handCount,
                        ["raw"] = raw,
                    });
                    return DispatchResult.HookFailed;
                }

                int commitStateCode = ReadStateCode(commitUnit);
                int commitHandCount = ReadCurrentHandCount(commitUnit);
                bool addonReacquired = (nint)commitUnit != selectUnitAddress;

                var v7 = stackalloc AtkValue[2];
                v7[0].SetInt(7);
                v7[1].SetInt(slotIndex);
                bool commitOk = commitUnit->FireCallback(2, v7, true);

                LastDiscardPath =
                    $"opcode-15+7(raw={raw},slot={slotIndex},selectOk={selectOk},commitOk={commitOk}," +
                    $"state={stateCode}->{commitStateCode},hand={handCount}->{commitHandCount},reacquired={addonReacquired})";
                trace?.Record("dispatcher.discard.exit", new Dictionary<string, object?>
                {
                    ["result"] = "Ok",
                    ["path"] = LastDiscardPath,
                    ["state"] = stateCode,
                    ["hand"] = handCount,
                    ["commit_state"] = commitStateCode,
                    ["commit_hand"] = commitHandCount,
                    ["raw"] = raw,
                    ["select_ok"] = selectOk,
                    ["commit_ok"] = commitOk,
                    ["addon_reacquired"] = addonReacquired,
                });
                return DispatchResult.Ok;
            }
        }

        // Post-call list-widget paths (state-6 hand=11/8/5, state-28 chi list).
        // The list items here ARE the discardable surface and SelectItem
        // commits cleanly (verified across post-chi/pon discard popups
        // 2026-05-10..05-18). SelectItem is void so we can't capture a
        // game-side ack; report Ok and rely on the FSM context-suppression
        // plus the snapshot-derived hand-shrink signal to gate retries.
        if (IsListWidgetPopupActive(unit) && TryDispatchListItemClick(unit, slotIndex))
        {
            LastDiscardPath = $"list-widget(slot={slotIndex})";
            trace?.Record("dispatcher.discard.exit", new Dictionary<string, object?> { ["result"] = "Ok", ["path"] = LastDiscardPath, ["state"] = stateCode, ["hand"] = handCount });
            return DispatchResult.Ok;
        }

        // Last-resort fallback: fire just opcode-7 in case we landed on an
        // un-mapped state where the tile-click handshake doesn't apply.
        // Returns HookFailed honestly so the FSM clears context and we don't
        // retry the same dead path for 3 seconds.
        var values = stackalloc AtkValue[2];
        values[0].SetInt(7);
        values[1].SetInt(slotIndex);
        bool ok = unit->FireCallback(2, values, true);
        LastDiscardPath = $"opcode-7-fallback(slot={slotIndex},ok={ok})";
        trace?.Record("dispatcher.discard.exit", new Dictionary<string, object?> { ["result"] = ok ? "Ok" : "HookFailed", ["path"] = LastDiscardPath, ["state"] = stateCode, ["hand"] = handCount });
        return ok ? DispatchResult.Ok : DispatchResult.HookFailed;
    }

    internal static bool IsTileClickDiscardSurface(
        int stateCode,
        int handCount,
        int selfDeclareListCode,
        int callPromptCode,
        int ourTurnDiscardCode)
    {
        return handCount > 0
            && handCount % 3 == 2
            && (stateCode == selfDeclareListCode
                || stateCode == callPromptCode
                || stateCode == ourTurnDiscardCode);
    }

    private static unsafe int ReadStateCode(AtkUnitBase* unit)
    {
        if (unit->AtkValues == null || unit->AtkValuesCount == 0)
            return -1;
        var v = unit->AtkValues[0];
        return v.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int ? v.Int : -1;
    }

    private unsafe int ReadHandSlotRaw(AtkUnitBase* unit, int slotIndex)
    {
        if (slotIndex is < 0 or > 13)
            return 0;
        byte* basePtr = (byte*)unit;
        return *(int*)(basePtr + HandArrayStartOffset + slotIndex * 4);
    }

    /// <summary>Counts non-zero hand-array slots (0..14). Scans the full array since post-call layouts park the claimed tile at slot 13 with [10..12] empty, so zero-terminating would miscount.</summary>
    private unsafe int ReadCurrentHandCount(AtkUnitBase* unit)
    {
        byte* basePtr = (byte*)unit;
        int offset = HandArrayStartOffset;
        int count = 0;
        for (int i = 0; i < 14; i++)
        {
            int raw = *(int*)(basePtr + offset + i * 4);
            if (raw != 0)
                count++;
        }
        return count;
    }

    /// <summary>
    /// True when the call-modal host (node 104) is visible and its inner
    /// shell (node 3) is an AtkComponentList. Distinguishes the list-widget
    /// popups (state-6 SelfDeclareList, state-28 CallPromptList) from the
    /// in-hand discard surface (state-30, no modal node) and from classic
    /// button-row popups (state-15 with string labels).
    /// </summary>
    private static unsafe bool IsListWidgetPopupActive(AtkUnitBase* unit)
    {
        var host = unit->GetNodeById(104);
        if (host == null || (int)host->Type < 1000)
            return false;
        if (!host->IsVisible())
            return false;
        var hostComp = ((AtkComponentNode*)host)->Component;
        if (hostComp == null)
            return false;
        var shell = hostComp->GetNodeById(3);
        return shell != null && (int)shell->Type == 1030;
    }

    /// <summary>
    /// Select option <paramref name="option"/> on the currently-active call prompt.
    /// Option numbers are button-order (leftmost = 0):
    ///   pon/pass prompt:    0 = Pon, 1 = Pass
    ///   chi/pass prompt:    0 = Chi, 1 = Pass
    ///   chi multi-sequence: 0..N = chi variants, N+1 = Pass
    ///   riichi (state 6):   0 = Riichi, 1 = Pass — same payload, different state code
    /// "Pass" is always the RIGHTMOST option.
    ///
    /// <para>Return value note: FireCallback returns <c>false</c> for the call-prompt
    /// opcode (11) even on manual in-game clicks that the game visibly accepts —
    /// verified by capturing pon/chi/riichi/tsumo button presses with the capture
    /// hook, which all logged <c>result=False</c> despite the pon/chi/riichi/tsumo
    /// actually firing. The return value is not a success signal for this opcode, so
    /// we ignore it and always report <see cref="DispatchResult.Ok"/>. The caller is
    /// expected to have verified the modal-visibility gate before dispatching —
    /// that's the real "should we click" predicate.</para>
    /// </summary>
    public unsafe DispatchResult DispatchCallOption(int option)
    {
        trace?.Record("dispatcher.call_option.enter", new Dictionary<string, object?> { ["option"] = option });
        if (!addon.TryGet(out var unit, out _))
        {
            trace?.Record("dispatcher.call_option.exit", new Dictionary<string, object?> { ["option"] = option, ["result"] = "AddonNotFound" });
            return DispatchResult.AddonNotFound;
        }
        if (!unit->IsVisible)
        {
            trace?.Record("dispatcher.call_option.exit", new Dictionary<string, object?> { ["option"] = option, ["result"] = "AddonNotVisible" });
            return DispatchResult.AddonNotVisible;
        }

        // Both state-15 classic popups (pon/chi/kan/ron + pass button row) and
        // state-6/28 list-widget popups (standalone Riichi/Pass) share the same
        // AtkComponentList shell type (1030), so the shell-type check alone
        // can't tell them apart. The reliable discriminator is parent AtkValues:
        // state-15 prompts put the button labels ("Pon", "Chi", "Pass", ...) as
        // plain Strings at low indices; state-6/28 prompts put only Ints/Bools
        // there with labels living inside the list items' text nodes.
        //
        // Dispatch accordingly:
        //  - Classic button-row: FireCallback([11, opt]) — what the game's own
        //    click handler ends up firing for a button press.
        //  - List widget: route through the AtkComponentList's SelectItem vfunc
        //    with dispatchEvent: true so the internal CallBackInterface runs
        //    (mouse-up → ListItemClick → commit). FireCallback alone on a list
        //    widget only plays the cosmetic declaration animation without
        //    committing state, which is what broke v0.0.0.16/.17.
        //
        // v0.0.0.18 routed everything through SelectItem and broke state-15
        // (pon/chi/ron) because SelectItem doesn't fire the addon-level opcode-11
        // callback the button-row handler expects. Distinguishing the two cases
        // restores state-15 behavior while keeping the state-6/28 fix.
        // The self-declare popup (state 6) can expose the visible strings
        // "Riichi" and "Pass" in the parent AtkValues even though the actual
        // controls are list items. Checking labels first therefore misroutes
        // this popup through opcode 11: FireCallback reports no usable failure,
        // but the popup remains open and autoplay appears frozen.
        //
        // State code is the reliable discriminator here. Always use the native
        // list selection path for SelfDeclareList before considering classic
        // button labels. State-15 Pon/Chi/Ron prompts continue through opcode 11.
        int stateCode = ReadStateCode(unit);
        bool riichiPassPrompt = HasAnyPromptLabel(unit, "Riichi", "リーチ")
            && HasAnyPromptLabel(unit, "Pass", "パス");
        // Route by the capture-verified state code first. This is locale independent:
        // state 15 is the classic button row, while state 6/28 are list widgets.
        bool classicButtonRow = stateCode == CallPromptCode;
        bool listWidgetPrompt = stateCode == SelfDeclareListCode || stateCode == CallPromptListCode;
        bool classicLabels = HasClassicButtonLabels(unit);
        trace?.Record("dispatcher.call_option.surface", new Dictionary<string, object?>
        {
            ["option"] = option,
            ["state"] = stateCode,
            ["riichi_pass_prompt"] = riichiPassPrompt,
            ["classic_button_row"] = classicButtonRow,
            ["list_widget_prompt"] = listWidgetPrompt,
            ["classic_labels"] = classicLabels,
            ["atk_value_count"] = unit->AtkValuesCount,
        });
        if (listWidgetPrompt || riichiPassPrompt)
        {
            // State-6 Riichi/Pass is a real AtkComponentList surface. SelectItem
            // performs the native row-selection stage, but the Japanese client does
            // not emit the addon-level opcode-11 commit from this programmatic call.
            // AutoPlayLoop schedules CommitSelectedCallOption on the following
            // framework tick for Riichi. The two stages must not run in the same tick.
            // State-28 continues to use SelectItem as its complete list dispatch.
            bool listOk = TryDispatchListItemClick(unit, option);
            trace?.Record("dispatcher.call_option.exit", new Dictionary<string, object?>
            {
                ["option"] = option, ["state"] = stateCode, ["path"] = "list-item",
                ["result"] = listOk ? "Ok" : "HookFailed",
            });
            return listOk ? DispatchResult.Ok : DispatchResult.HookFailed;
        }

        if (classicButtonRow || classicLabels)
        {
            var values = stackalloc AtkValue[2];
            values[0].SetInt(11);
            values[1].SetInt(option);
            trace?.Record("dispatcher.fire_callback.pre", new Dictionary<string, object?> { ["opcode"] = 11, ["option"] = option, ["state"] = stateCode, ["path"] = "classic-button" });
            unit->FireCallback(2, values, true);
            trace?.Record("dispatcher.call_option.exit", new Dictionary<string, object?> { ["option"] = option, ["state"] = stateCode, ["path"] = "classic-button", ["result"] = "Ok" });
            return DispatchResult.Ok;
        }

        if (TryDispatchListItemClick(unit, option))
        {
            trace?.Record("dispatcher.call_option.exit", new Dictionary<string, object?> { ["option"] = option, ["state"] = stateCode, ["path"] = "list-item-fallback", ["result"] = "Ok" });
            return DispatchResult.Ok;
        }

        // Fallback if the shell isn't a list widget either — keep the legacy
        // FireCallback path so we don't silently drop the dispatch.
        var fallback = stackalloc AtkValue[2];
        fallback[0].SetInt(11);
        fallback[1].SetInt(option);
        trace?.Record("dispatcher.fire_callback.pre", new Dictionary<string, object?> { ["opcode"] = 11, ["option"] = option, ["state"] = stateCode, ["path"] = "legacy-fallback" });
        unit->FireCallback(2, fallback, true);
        trace?.Record("dispatcher.call_option.exit", new Dictionary<string, object?> { ["option"] = option, ["state"] = stateCode, ["path"] = "legacy-fallback", ["result"] = "Ok" });
        return DispatchResult.Ok;
    }

    /// <summary>
    /// Returns true only after the initial Riichi list selection has been accepted
    /// and EMJ has replaced the Riichi/Pass rows with the discard-candidate list.
    ///
    /// The transition is exposed in AtkValues as state=SelfDeclareList followed by
    /// a positive candidate count and that many slot-like integer entries. The
    /// initial Riichi/Pass surface reports a zero count, so this is a locale-
    /// independent completion signal and avoids re-selecting Riichi.
    /// </summary>
    public unsafe bool IsRiichiDiscardSelectionSurface()
    {
        if (!addon.TryGet(out var unit, out _) || !unit->IsVisible)
            return false;
        if (ReadStateCode(unit) != SelfDeclareListCode)
            return false;

        var values = unit->AtkValues;
        int valueCount = unit->AtkValuesCount;
        if (values == null || valueCount < 3 || values[1].Type != AtkValueType.Int)
            return false;

        int candidateCount = values[1].Int;
        if (candidateCount <= 0 || candidateCount > 14 || valueCount < candidateCount + 2)
            return false;

        for (int i = 0; i < candidateCount; i++)
        {
            var candidate = values[2 + i];
            if (candidate.Type != AtkValueType.Int || candidate.Int is < 0 or > 13)
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when parent AtkValues carry a bare button-label string like
    /// "Pon"/"Chi"/"Kan"/"Ron"/"Riichi"/"Tsumo"/"Pass" in the first ~20 slots —
    /// the signature of a state-15 classic button-row popup. State-6/28
    /// list-widget popups carry only Ints/Bools there (labels live inside
    /// list-item children), so a false from this check routes dispatch to the
    /// SelectItem path.
    /// </summary>

    private static unsafe bool HasAnyPromptLabel(AtkUnitBase* unit, params string[] expectedLabels)
    {
        var atkValues = unit->AtkValues;
        if (atkValues == null)
            return false;
        int scanEnd = Math.Min((int)unit->AtkValuesCount, 32);
        for (int i = 0; i < scanEnd; i++)
        {
            var v = atkValues[i];
            if (v.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String &&
                v.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ConstString &&
                v.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ManagedString)
                continue;
            if (v.String.Value == null)
                continue;
            string actual = v.String.ToString();
            foreach (string expected in expectedLabels)
            {
                if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static unsafe bool HasClassicButtonLabels(AtkUnitBase* unit)
    {
        var atkValues = unit->AtkValues;
        if (atkValues == null)
            return false;
        int scanEnd = Math.Min((int)unit->AtkValuesCount, 20);
        for (int i = 0; i < scanEnd; i++)
        {
            var v = atkValues[i];
            if (v.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String &&
                v.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ConstString &&
                v.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ManagedString)
                continue;
            if (v.String.Value == null)
                continue;
            var s = v.String.ToString();
            switch (s)
            {
                case "Pon":
                case "ポン":
                case "Chi":
                case "チー":
                case "Kan":
                case "カン":
                case "Ron":
                case "ロン":
                case "Riichi":
                case "リーチ":
                case "Tsumo":
                case "ツモ":
                case "Pass":
                case "パス":
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// If the modal shell is an AtkComponentList, dispatch the click through
    /// the list's native <c>SelectItem(index, dispatchEvent: true)</c> — same
    /// code path a mouse-up runs into. Returns true when handled, false when
    /// the shell isn't a list and the caller should fall back.
    /// </summary>
    private static unsafe bool TryDispatchListItemClick(AtkUnitBase* unit, int option)
    {
        var host = unit->GetNodeById(104);
        if (host == null || (int)host->Type < 1000)
            return false;
        var hostComp = ((AtkComponentNode*)host)->Component;
        if (hostComp == null)
            return false;
        var shell = hostComp->GetNodeById(3);
        if (shell == null || (int)shell->Type != 1030)
            return false;
        var shellComp = ((AtkComponentNode*)shell)->Component;
        if (shellComp == null)
            return false;
        var list = (FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentList*)shellComp;
        list->SelectItem(option, dispatchEvent: true);
        return true;
    }


    /// <summary>
    /// Commits the currently selected option on the self-declare list after the
    /// list item has received its native SelectItem event. EMJ needs these as two
    /// separate UI ticks on some clients; sending both in the same frame is ignored.
    /// </summary>
    public unsafe DispatchResult CommitSelectedCallOption(int option)
    {
        trace?.Record("dispatcher.call_option_commit.enter", new Dictionary<string, object?> { ["option"] = option });
        if (!addon.TryGet(out var unit, out _))
        {
            trace?.Record("dispatcher.call_option_commit.exit", new Dictionary<string, object?>
            {
                ["option"] = option,
                ["result"] = "AddonNotFound",
            });
            return DispatchResult.AddonNotFound;
        }
        if (!unit->IsVisible)
        {
            trace?.Record("dispatcher.call_option_commit.exit", new Dictionary<string, object?>
            {
                ["option"] = option,
                ["result"] = "AddonNotVisible",
            });
            return DispatchResult.AddonNotVisible;
        }

        int stateCode = ReadStateCode(unit);
        if (stateCode != SelfDeclareListCode)
        {
            trace?.Record("dispatcher.call_option_commit.exit", new Dictionary<string, object?>
            {
                ["option"] = option,
                ["state"] = stateCode,
                ["result"] = "HookFailed",
                ["reason"] = "not-self-declare-list",
            });
            return DispatchResult.HookFailed;
        }

        var values = stackalloc AtkValue[2];
        values[0].SetInt(11);
        values[1].SetInt(option);
        bool callbackResult = unit->FireCallback(2, values, true);
        trace?.Record("dispatcher.call_option_commit.exit", new Dictionary<string, object?>
        {
            ["option"] = option,
            ["state"] = stateCode,
            ["path"] = "opcode-11-after-list-select",
            ["callback_result"] = callbackResult,
            ["result"] = "Ok",
        });
        // Opcode 11 returns false even when the game accepts it; the structural
        // AtkValue transition is the authoritative success signal.
        return DispatchResult.Ok;
    }

    /// <summary>
    /// Pass on a call prompt. Option 1 = Pass (rightmost button). Confirmed by observation:
    /// pon/pass and chi/pass prompts both show [Call][Pass] order, so pass is always opt 1.
    /// No fallback — if this fails we return HookFailed; fallback to option 0 would
    /// accidentally fire the call action (undesired).
    /// </summary>
    public DispatchResult DispatchPass() => DispatchCallOption(1);

    /// <summary>
    /// Accept a pon/chi/kan call by clicking the leftmost button (option 0). The game
    /// knows from context which call is offered — we just fire option 0. For chi
    /// prompts with multiple sequence variants, option 0 picks the first (lowest)
    /// sequence; we'd need a specific override for non-default variants.
    /// </summary>
    public DispatchResult DispatchCall() => DispatchCallOption(0);

    /// <summary>List-index of <paramref name="target"/> in the rendered hand, for UI highlight callers. Prefers index 13 when hand is full. For addon-slot resolution (dispatch path) use <c>AddonEmjReader.FindAddonSlotOfTile</c>.</summary>
    public static int FindSlotOfTile(Tile target, System.Collections.Generic.IReadOnlyList<Tile> hand)
    {
        if (hand.Count == 14 && hand[13].Id == target.Id)
            return 13;
        for (int i = 0; i < hand.Count; i++)
            if (hand[i].Id == target.Id)
                return i;
        return -1;
    }

    /// <summary>
    /// Opcode constants for FireCallback's first AtkValue. All values here
    /// are corpus-confirmed from the inputs telemetry stream.
    ///
    /// Discard (15+7) is the two-callback handshake captured 2026-05-23.
    /// CallPrompt (11) handles every popup button: Pon, Chi (multi-variant),
    /// MinKan, ShouMinKan, AnKan, Ron, Riichi (declaration click), Tsumo, and
    /// Pass.
    ///
    /// Historical note: opcodes 8 (Riichi), 9 (Tsumo), 10 (Ron), and 12 (Kan)
    /// were shipped as speculative dispatchers across v0.1.0.11..v0.1.1.1.
    /// Field bug #39 (2026-05-24) caught the Ron path corrupting state into
    /// the DRAW screen; bug #40 (2026-05-25) caught the Tsumo path firing
    /// <c>FireCallback(1, [Int=9])</c> 50+ times at state-6 SelfDeclareList
    /// with result=false and zero state movement — same class. The corpus
    /// records for opcode 9 were the addon's own internal callback fired
    /// *after* a SelectItem(0) click on the Tsumo list item, not a click-
    /// equivalent payload our plugin could replay standalone. Ron / AnKan /
    /// ShouMinKan / Riichi declaration / Tsumo now all flow through the
    /// corpus-confirmed call-prompt button-row path (opcode 11) — which
    /// <see cref="DispatchCallOption"/> auto-routes to <c>SelectItem</c> on
    /// list-widget popups (state-6/28) and to <c>FireCallback([11, opt])</c>
    /// on classic button-row popups (state-15).
    /// </summary>
    private static class Opcode
    {
        public const int Discard = 7;
        public const int CallPrompt = 11;
    }

    /// <summary>
    /// Pick an exact meld pattern on the state-25 call-pattern sub-popup. Opcode 12 +
    /// variant index — captured 2026-05-25 from a manual click on a 2-variant
    /// chi popup (hand=4778m123568p225s, fire_args=[12, 0], state=30). The
    /// popup's parent AtkValues carry a "Chi" string label at slot 2, which
    /// makes <see cref="DispatchCallOption"/> misroute it through the opcode-11
    /// button-row path — that's why three [11,0] dispatches silently no-opped
    /// and the bot froze. Routes around the label heuristic by firing opcode
    /// 12 directly.
    /// </summary>
    public unsafe DispatchResult DispatchCallVariant(int variantIndex)
    {
        trace?.Record("dispatcher.call_variant.enter", new Dictionary<string, object?> { ["variant"] = variantIndex });
        if (!addon.TryGet(out var unit, out _))
            return DispatchResult.AddonNotFound;
        if (!unit->IsVisible)
            return DispatchResult.AddonNotVisible;

        var values = stackalloc AtkValue[2];
        values[0].SetInt(12);
        values[1].SetInt(variantIndex);
        unit->FireCallback(2, values, true);
        return DispatchResult.Ok;
    }

    public DispatchResult DispatchChiVariant(int variantIndex) => DispatchCallVariant(variantIndex);

    /// <summary>Dismisses the post-hand agari/draw result modal (state-29 "Next"). Routes through ReceiveEvent(ButtonClick) rather than FireCallback — the captured `[14]` was the addon's notification *after* the click, not the trigger; firing it directly landed the addon in stuck state-32 (2026-05-26).</summary>
    public unsafe DispatchResult DispatchHandResultNext()
    {
        trace?.Record("dispatcher.hand_result_next.enter");
        const uint nextButtonNodeId = 97;
        const uint nextButtonCollisionId = 4;
        const int nextButtonEventParam = 7;

        if (!addon.TryGet(out var unit, out _))
            return DispatchResult.AddonNotFound;
        if (!unit->IsVisible)
            return DispatchResult.AddonNotVisible;

        var btnNode = unit->GetNodeById(nextButtonNodeId);
        if (btnNode == null || (int)btnNode->Type < 1000)
            return DispatchResult.HookFailed;
        var compNode = (AtkComponentNode*)btnNode;
        if (compNode->Component == null)
            return DispatchResult.HookFailed;
        var collision = compNode->Component->UldManager.SearchNodeById(nextButtonCollisionId);
        if (collision == null)
            return DispatchResult.HookFailed;

        var atkEvent = new AtkEvent
        {
            Listener = (AtkEventListener*)compNode->Component,
            Node = collision,
            Target = (AtkEventTarget*)collision,
        };
        unit->ReceiveEvent(AtkEventType.ButtonClick, nextButtonEventParam, &atkEvent);
        return DispatchResult.Ok;
    }
}
