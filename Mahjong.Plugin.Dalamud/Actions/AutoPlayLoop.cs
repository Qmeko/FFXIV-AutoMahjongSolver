using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Policy;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace Mahjong.Plugin.Dalamud.Actions;

public sealed class AutoPlayLoop : IDisposable
{
    private const int CallVariantSelectStateCode = 25;
    /// <summary>Agari / draw result modal between hands; carries Legal=None and a single "Next" button.</summary>
    private const int HandResultStateCode = 29;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(10.0);

    private const int VariantAcceptDelayMs = 500;
    private const int CallDecisionDelayMs = 700;
    private const int RiichiTsumogiriDelayMs = 700;
    private const int HandResultAdvanceDelayMs = 300;
    /// <summary>State-29 must persist this long before we fire the Next click. Firing during the result-modal animation phase landed the addon in a stuck state-32 with no inputs accepted (2026-05-26).</summary>
    private static readonly TimeSpan HandResultStabilityWindow = TimeSpan.FromSeconds(3.5);

    private readonly Plugin plugin;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly MahjongAddon addon;
    private readonly ActionStateMachine fsm = new(DispatchTimeout, RetryCooldown);
    private bool disposed;
    private string? lastSkipReason;
    private DateTime? handResultFirstSeenAt;
    private bool handResultDispatchedThisInstance;
    private ActionChoice? pendingVariantChoice;
    private DispatchContext? timingContext;
    private DateTime timingContextFirstSeenUtc;
    private string? lastActionFingerprint;
    private DateTime lastActionFingerprintAtUtc;
    private string? lastDiagnosticFingerprint;
    private PendingPostCallDiscard? pendingPostCallDiscard;
    private ActionOperationTransaction? activeOperation;
    private bool automationStateWasEnabled;

    private readonly record struct PendingPostCallDiscard(
        Tile Tile,
        ActionKind CallKind,
        int OwnDiscardCountAtCommit,
        bool DispatchSent);

    public string LastActionDescription { get; private set; } = "(none)";

    public int LastObservedState { get; private set; } = -1;

    public int LastObservedHandCount { get; private set; } = -1;

    public AutoPlayLoop(Plugin plugin, IFramework framework, IPluginLog log, MahjongAddon addon)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(addon);
        this.plugin = plugin;
        this.framework = framework;
        this.log = log;
        this.addon = addon;
        plugin.AgentEmjEventProbe.EventObserved += OnAgentEmjEvent;
        framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        framework.Update -= OnUpdate;
        plugin.AgentEmjEventProbe.EventObserved -= OnAgentEmjEvent;
        ResetOperationState("dispose");
    }

    private unsafe void OnUpdate(IFramework fw)
    {
        plugin.ExecutionTrace.Record("autoplay.tick.enter", new Dictionary<string, object?> { ["disposed"] = disposed });
        if (disposed)
            return;

        var cfg = plugin.Configuration;
        plugin.ExecutionTrace.Record("autoplay.gates", new Dictionary<string, object?>
        {
            ["tos"] = cfg.TosAccepted, ["armed"] = cfg.AutomationArmed, ["suggestion_only"] = cfg.SuggestionOnly,
            ["auto_call"] = cfg.AutoCallEnabled, ["auto_advance"] = cfg.AutoAdvanceAfterHand,
        });
        bool callOnlyMode = cfg.TosAccepted
            && cfg.AutomationArmed
            && cfg.SuggestionOnly
            && cfg.AutoCallEnabled;

        bool automationEnabled = IsAutomationLoopEnabled();
        if (!automationEnabled)
        {
            if (automationStateWasEnabled)
                ResetOperationState("automation-disabled");
            automationStateWasEnabled = false;
            EmitSkipReason($"gate: tos={cfg.TosAccepted} armed={cfg.AutomationArmed} suggest_only={cfg.SuggestionOnly} auto_calls={cfg.AutoCallEnabled}",
                state: -1, hand: -1, flags: 0);
            return;
        }
        automationStateWasEnabled = true;

        if (!ContinueAfterStuckRecovery())
        {
            EmitSkipReason("dispatch in flight (still within timeout)",
                state: -1, hand: -1, flags: 0);
            return;
        }

        // Runs before the snapshot guard: the result modal has no hand-array, so TryBuildSnapshot returns null and the post-snapshot state checks never fire.
        if (!callOnlyMode && plugin.Configuration.AutoAdvanceAfterHand)
        {
            int earlyState = ReadStateCode();
            if (TryHandleHandResult(earlyState))
                return;
        }

        var snap = plugin.AddonReader.TryBuildSnapshot();
        plugin.ExecutionTrace.Record("autoplay.snapshot", snap is null ? new Dictionary<string, object?> { ["available"] = false } : new Dictionary<string, object?>
        {
            ["available"] = true, ["state"] = snap.AddonStateCode, ["hand"] = snap.Hand.Count, ["wall"] = snap.WallRemaining,
            ["legal"] = snap.Legal.Flags.ToString(), ["discardable"] = string.Join(",", snap.Legal.DiscardableTiles),
            ["pon_count"] = snap.Legal.PonCandidates.Count, ["chi_count"] = snap.Legal.ChiCandidates.Count, ["kan_count"] = snap.Legal.KanCandidates.Count,
            ["our_melds"] = snap.OurMelds.Count, ["turn"] = snap.TurnIndex,
        });

        // Advance the retained operation only from an authoritative snapshot; transient nulls preserve it unchanged.
        CheckPendingDispatchOutcome(snap);

        CheckStuckStateAndEmit(snap, callOnlyMode);

        if (snap is null)
        {
            // Do not clear FSM context on transient snapshot misses — that would break the retry-cooldown debounce.
            EmitSkipReason("snapshot unavailable", state: -1, hand: -1, flags: 0);
            return;
        }

        // A single operation transaction owns the UI until the game publishes
        // structural evidence. State 25 is the verified multi-chi continuation
        // and is the only surface allowed to advance an in-flight open call.
        if (activeOperation is { IsTerminal: false } inFlight
            && !(inFlight.Kind == ActionOperationKind.OpenCall
                && snap.AddonStateCode == CallVariantSelectStateCode))
        {
            EmitSkipReason(
                $"operation pending ({inFlight.Label}/{inFlight.Kind}/{inFlight.Phase}, state={snap.AddonStateCode} hand={snap.Hand.Count})",
                state: snap.AddonStateCode,
                hand: snap.Hand.Count,
                flags: (int)snap.Legal.Flags);
            return;
        }

        // Akochan returns [Chi/Pon, mandatory dahai] as one atomic response.
        // Once the call is structurally committed, execute that retained discard
        // before routing the still-visible call surface through policy again.
        // The mandatory discard returned together with Chi/Pon is the second
        // half of the already accepted call transaction. It must be completed
        // even when the loop is in call-only mode; otherwise AutoCall can commit
        // the meld and then strand EMJ forever on the 11/8/5/2-tile discard
        // surface. This is not a new autonomous discard decision.
        if (TryDispatchPendingPostCallDiscard(snap))
            return;

        int state = ReadStateCode();
        bool preliminaryDiscardTurn = snap.Legal.Can(ActionFlags.Discard);
        int contextState = NormalizeDispatchState(state, preliminaryDiscardTurn);
        var context = new DispatchContext(contextState, snap.Hand.Count);
        if (!timingContext.HasValue || !timingContext.Value.Equals(context))
        {
            timingContext = context;
            timingContextFirstSeenUtc = DateTime.UtcNow;
        }
        LastObservedState = state;
        LastObservedHandCount = context.Hand;

        if (state == CallVariantSelectStateCode)
        {
            if (callOnlyMode && pendingVariantChoice is null)
            {
                EmitSkipReason("hints mode: no automatic call variant pending",
                    state: state, hand: snap.Hand.Count, flags: (int)snap.Legal.Flags);
                return;
            }

            EmitProgressing();
            HandleCallVariantSelect(context);
            return;
        }

        bool isDiscardTurn = snap.Legal.Can(ActionFlags.Discard);
        bool isExternalCallPrompt = IsExternalCallPromptSurface(snap);
        // Riichi is offered on the same state-6 surface as an ordinary self discard.
        // When Discard is legal, route the surface through ScheduleDiscard so an AI
        // decision of either Discard (decline riichi) or Riichi can be executed.
        // Treating the Riichi flag itself as a call prompt stranded Akochan's
        // deliberate non-riichi discard on legal=Discard,Riichi[,Pass].
        const ActionFlags selfActionSurface =
            ActionFlags.AnKan | ActionFlags.ShouMinKan |
            ActionFlags.Ron | ActionFlags.Tsumo;
        bool isCallPrompt = isExternalCallPrompt
            || (snap.Legal.Flags & selfActionSurface) != 0
            || (!isDiscardTurn && snap.Legal.Can(ActionFlags.Riichi));
        if (callOnlyMode)
        {
            const ActionFlags automaticSelfCallSurface =
                ActionFlags.AnKan | ActionFlags.ShouMinKan;
            isCallPrompt = isExternalCallPrompt
                || (snap.Legal.Flags & automaticSelfCallSurface) != 0;
            isDiscardTurn = false;
        }
        int flags = (int)snap.Legal.Flags;

        // After Riichi has been accepted, EMJ can retain a stale Riichi/Pass
        // signature on state 15 for several opponent turns. It is not a new
        // declaration prompt. Never send another option-0 callback once our
        // public state already confirms Riichi. Ron/Pass remains actionable
        // because it carries the Ron flag and therefore does not match this gate.
        if (snap.OurRiichi
            && snap.Legal.Can(ActionFlags.Riichi)
            && (snap.Legal.Flags & ~(ActionFlags.Riichi | ActionFlags.Pass)) == ActionFlags.None)
        {
            fsm.ClearRiichiConfirm();
            EmitSkipReason($"stale post-riichi surface (state={state} legal={snap.Legal.Flags})",
                state: state, hand: snap.Hand.Count, flags: flags);
            return;
        }

        // State 30 is ambiguous. It is used both after a committed discard and
        // after opcode 15 has only selected/highlighted a tile. The latter still
        // requires opcode 7. Suppress state 30 only while a discard dispatched by
        // this loop is awaiting structural proof (our river grows or the hand
        // shrinks). A state-30 surface with no such pending dispatch remains
        // actionable, allowing an authoritative AI result to complete a partial
        // manual/external selection instead of freezing forever.
        bool pendingStructuralDiscard = activeOperation is
            { IsTerminal: false, Kind: ActionOperationKind.Discard or ActionOperationKind.PostCallDiscard };
        if (state == 30 && isDiscardTurn && !isCallPrompt
            && ShouldSuppressState30DiscardSurface(pendingStructuralDiscard))
        {
            EmitSkipReason($"awaiting discard structural commit (state={state} hand={snap.Hand.Count})",
                state: state, hand: snap.Hand.Count, flags: flags);
            return;
        }

        // Riichi-confirm latch is hand-scoped via ObserveWall — popup signature drops mid-hand and clearing per-tick would let the loop redeclare riichi 20+ times in one hand.
        fsm.ObserveWall(snap.WallRemaining);

        if (!isCallPrompt && !isDiscardTurn)
        {
            // Do not clear FSM context on transient "not actionable" ticks — discard-animation gaps drop the Discard flag mid-commit and clearing here permits a duplicate dispatch.
            EmitSkipReason($"not actionable (state={state} hand={snap.Hand.Count} legal={snap.Legal.Flags})",
                state: state, hand: snap.Hand.Count, flags: flags);
            return;
        }

        if (fsm.ShouldSuppressForContext(context, DateTime.UtcNow))
        {
            EmitSkipReason($"suppressed for context (state={context.State} hand={context.Hand})",
                state: state, hand: snap.Hand.Count, flags: flags);
            return;
        }

        if (TryHandleRiichiConfirmTsumogiri(snap, context))
        {
            EmitProgressing();
            return;
        }

        EmitProgressing();
        if (isCallPrompt)
            ScheduleCallDecision(context);
        else
            ScheduleDiscard(context);
    }


    internal static bool IsExternalCallPromptSurface(StateSnapshot snap) =>
        !snap.Legal.Can(ActionFlags.Discard)
        && snap.Hand.Count > 0
        && snap.Hand.Count % 3 == 1
        // A visible Pass-only prompt is still actionable. Requiring Pon/Chi/Kan
        // stranded Pass, Ron and temporarily incomplete Japanese call surfaces.
        && snap.Legal.Can(ActionFlags.Pass);

    /// <summary>
    /// EMJ state 6/22/30 are transient surfaces of the same self-discard turn.
    /// Treat them as one dispatch context so a UI transition cannot schedule the
    /// same decision a second time.
    /// </summary>
    private static int NormalizeDispatchState(int state, bool isDiscardTurn) =>
        isDiscardTurn && state is 6 or 22 or 30 ? 6 : state;

    /// <summary>Dedup by exact reason string — loop ticks 60x/sec, so emit only on transitions.</summary>
    private void EmitSkipReason(string reason, int state, int hand, int flags)
    {
        if (lastSkipReason == reason)
            return;
        lastSkipReason = reason;
        log.Info($"[AutoPlayLoop] skip: {reason}");
        plugin.ExecutionTrace.Record("autoplay.skip", new Dictionary<string, object?> { ["reason"] = reason, ["state"] = state, ["hand"] = hand, ["flags"] = flags });
        plugin.FindingsLog?.Record("hand_state_paused", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["state"] = state,
            ["hand"] = hand,
            ["flags"] = flags,
        });
    }

    private void EmitProgressing()
    {
        if (lastSkipReason is null)
            return;
        log.Info($"[AutoPlayLoop] resumed (was: {lastSkipReason})");
        lastSkipReason = null;
    }

    private void EmitDiagnosticDecision(string phase, StateSnapshot snap, ActionChoice choice)
    {
        if (!plugin.Configuration.DiagnosticDecisionLogging)
            return;

        string source = "Unknown";
        long inferenceMs = -1;
        string fallback = string.Empty;
        string externalStatus = string.Empty;
        if (plugin.Policy is Mahjong.Plugin.Dalamud.ExternalAi.SelectablePolicy selectable)
        {
            source = selectable.LastDecisionSource;
            inferenceMs = selectable.LastInferenceMs;
            fallback = selectable.LastFallbackReason;
            externalStatus = selectable.ExternalStatus;
        }

        string seatShape = string.Join(",", snap.Seats.Select(s => $"{s.Riichi}:{s.DiscardCount}:{s.Melds.Count}"));
        string fingerprint = string.Join("|", new object?[]
        {
            phase,
            snap.AddonStateCode,
            snap.Hand.Count,
            snap.WallRemaining,
            (int)snap.Legal.Flags,
            seatShape,
            choice.Kind,
            choice.DiscardTile?.ToString() ?? "-",
        });
        if (fingerprint == lastDiagnosticFingerprint)
            return;
        lastDiagnosticFingerprint = fingerprint;

        static string Tiles(IEnumerable<Tile> tiles) => string.Join(',', tiles.Select(t => t.ToString()));
        static string Melds(IEnumerable<Meld> melds) => string.Join(';', melds.Select(m => m.ToString()));

        string scores = string.Join(',', snap.Scores);
        string hand = Tiles(snap.Hand);
        string dora = Tiles(snap.DoraIndicators);
        string ourMelds = Melds(snap.OurMelds);

        log.Info(
            $"[MahjongSnapshot] phase={phase} state={snap.AddonStateCode} handCount={snap.Hand.Count} " +
            $"ourSeat={snap.OurSeat} dealer={snap.DealerSeat} seatKnown={snap.SeatInfoKnown} " +
            $"roundWind={snap.RoundWind} honba={snap.Honba} riichiSticks={snap.RiichiSticks} " +
            $"wall={snap.WallRemaining} turn={snap.TurnIndex} scores=[{scores}] " +
            $"ourRiichi={snap.OurRiichi} ippatsu={snap.OurIppatsu} doubleRiichi={snap.OurDoubleRiichi} " +
            $"legal={snap.Legal.Flags} hand=[{hand}] dora=[{dora}] ourMelds=[{ourMelds}]");

        for (int i = 0; i < snap.Seats.Count; i++)
        {
            var seat = snap.Seats[i];
            log.Info(
                $"[OpponentSnapshot] seat={i} riichi={seat.Riichi} riichiDiscard={seat.RiichiDiscardIndex} " +
                $"ippatsu={seat.Ippatsu} tenpaiCalled={seat.IsTenpaiCalled} discardCount={seat.DiscardCount} " +
                $"discards=[{Tiles(seat.Discards)}] melds=[{Melds(seat.Melds)}]");
        }

        log.Info(
            $"[MortalInput] phase={phase} ourSeat={snap.OurSeat} dealer={snap.DealerSeat} " +
            $"roundWind={snap.RoundWind} honba={snap.Honba} kyotaku={snap.RiichiSticks} " +
            $"wall={snap.WallRemaining} legal={snap.Legal.Flags} scores=[{scores}]");

        if (snap.Legal.Can(ActionFlags.Riichi) || snap.Legal.DiscardableTiles.Count > 0 && snap.Hand.Count % 3 == 2)
        {
            string candidates = Tiles(snap.Legal.DiscardableTiles);
            log.Info(
                $"[RiichiCheck] available={snap.Legal.Can(ActionFlags.Riichi)} " +
                $"legal={snap.Legal.Flags} candidates=[{candidates}] handClosed={!snap.OurMelds.Any(m => m.IsOpen)} " +
                $"score={(snap.Scores.Count > snap.OurSeat ? snap.Scores[snap.OurSeat] : -1)} wall={snap.WallRemaining} " +
                $"mortalAction={choice.Kind} chosenTile={choice.DiscardTile?.ToString() ?? "-"} " +
                $"source={source} status={externalStatus}");
        }

        log.Info(
            $"[MortalDecision] phase={phase} source={source} inferenceMs={inferenceMs} " +
            $"action={choice.Kind} tile={choice.DiscardTile?.ToString() ?? "-"} " +
            $"reason={choice.Reasoning} status={externalStatus} " +
            $"fallback={(!string.IsNullOrWhiteSpace(fallback) ? fallback : "none")}");
    }

    private void EmitDecisionFinding(string source, StateSnapshot snap, ActionChoice choice)
    {
        plugin.ExecutionTrace.Record("decision.full", new Dictionary<string, object?>
        {
            ["source"] = source, ["kind"] = choice.Kind.ToString(), ["tile"] = choice.DiscardTile?.ToString(),
            ["reasoning"] = choice.Reasoning, ["state"] = snap.AddonStateCode, ["hand"] = snap.Hand.Count,
            ["legal"] = snap.Legal.Flags.ToString(), ["pon_count"] = snap.Legal.PonCandidates.Count,
            ["chi_count"] = snap.Legal.ChiCandidates.Count, ["kan_count"] = snap.Legal.KanCandidates.Count,
        });
        plugin.FindingsLog?.Record("decision", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["kind"] = choice.Kind.ToString(),
            ["tile"] = choice.DiscardTile?.ToString(),
            ["hand_count"] = snap.Hand.Count,
            ["flags"] = (int)snap.Legal.Flags,
            ["pon_candidates"] = snap.Legal.PonCandidates.Count,
            ["chi_candidates"] = snap.Legal.ChiCandidates.Count,
            ["kan_candidates"] = snap.Legal.KanCandidates.Count,
            ["wall"] = snap.WallRemaining,
            ["reasoning"] = choice.Reasoning,
        });
    }

    private void EmitDispatchFinding(
        string label, InputDispatcher.DispatchResult result,
        int? option = null, Tile? tile = null, int? slot = null, int? state = null,
        StateSnapshot? snap = null, ActionChoice? committedChoice = null,
        string? dispatchPath = null)
    {
        string path = dispatchPath
            ?? (slot.HasValue ? plugin.Dispatcher.LastDiscardPath : ResolvePromptDispatchPath(option));
        plugin.ExecutionTrace.Record("dispatch.attempt", new Dictionary<string, object?>
        {
            ["label"] = label, ["result"] = result.ToString(), ["option"] = option, ["tile"] = tile?.ToString(),
            ["slot"] = slot, ["state"] = state, ["path"] = path,
            ["current_state"] = snap?.AddonStateCode, ["current_hand"] = snap?.Hand.Count, ["current_legal"] = snap?.Legal.Flags.ToString(),
        });
        plugin.FindingsLog?.Record("dispatch_attempted", new Dictionary<string, object?>
        {
            ["label"] = label,
            ["result"] = result.ToString(),
            ["option"] = option,
            ["tile"] = tile?.ToString(),
            ["slot"] = slot,
            ["state"] = state,
            ["path"] = path,
            ["cur_state"] = snap?.AddonStateCode,
            ["cur_hand"] = snap?.Hand.Count,
            ["cur_melds"] = snap?.OurMelds.Count,
            ["cur_legal"] = snap?.Legal.Flags.ToString(),
        });

    }

    internal static string ResolvePromptDispatchPathForTest(int? option) =>
        ResolvePromptDispatchPath(option);

    private static string ResolvePromptDispatchPath(int? option) =>
        option.HasValue ? $"opcode-11(opt={option.Value})" : "(none)";

    internal static bool ShouldSuppressState30DiscardSurface(bool pendingStructuralDiscard) =>
        pendingStructuralDiscard;

    internal static bool HasStructuralDiscardCommitEvidence(
        int handAtDispatch,
        int ownDiscardCountAtDispatch,
        int currentHandCount,
        int currentOwnDiscardCount) =>
        currentHandCount < handAtDispatch
        || currentOwnDiscardCount > ownDiscardCountAtDispatch;

    // Start recovery before the 12-second user-visible instruction deadline.
    // This leaves four seconds for Mortal/Akochan process recreation, live-board
    // bootstrap and inference instead of waiting the full deadline to recover.
    private static readonly TimeSpan StuckStateThreshold = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InstructionDeadline = TimeSpan.FromSeconds(12);

    private int? stuckStateCode;
    private int? stuckHandCount;
    private ActionFlags? stuckLegal;
    private DateTime stuckSince;
    private bool stuckEmitted;

    private void CheckStuckStateAndEmit(StateSnapshot? snap, bool callOnlyMode)
    {
        if (snap is null)
            return;

        var legal = snap.Legal.Flags;

        // Hint mode with Auto Call enabled intentionally does not own ordinary
        // self-discard / riichi surfaces. The hint UI polls the selected AI and
        // displays the answer, while AutoPlayLoop only handles call prompts.
        // Treating a legal Discard surface as an automation stall caused the
        // selected Mortal process to be destroyed and recreated every eight
        // seconds, repeatedly erasing an otherwise valid hint and making manual
        // resync appear ineffective. Only monitor surfaces that this loop can
        // actually execute in call-only mode.
        if (callOnlyMode && activeOperation is null)
        {
            bool externalCallPrompt = IsExternalCallPromptSurface(snap);
            bool automaticSelfCallPrompt =
                (legal & (ActionFlags.AnKan | ActionFlags.ShouMinKan)) != 0;
            if (!externalCallPrompt && !automaticSelfCallPrompt)
            {
                stuckStateCode = null;
                stuckHandCount = null;
                stuckLegal = null;
                stuckEmitted = false;
                return;
            }
        }
        // A 13/10/7/4/1-tile hand with no legal action is the normal opponent
        // turn, not a stuck automation state. Only actionable surfaces or an
        // explicitly retained operation transaction are eligible for STUCK.
        if (legal == ActionFlags.None && activeOperation is null)
        {
            stuckStateCode = null;
            stuckHandCount = null;
            stuckLegal = null;
            stuckEmitted = false;
            return;
        }

        if (stuckStateCode != snap.AddonStateCode
            || stuckHandCount != snap.Hand.Count
            || stuckLegal != legal)
        {
            stuckStateCode = snap.AddonStateCode;
            stuckHandCount = snap.Hand.Count;
            stuckLegal = legal;
            stuckSince = DateTime.UtcNow;
            stuckEmitted = false;
            return;
        }

        if (stuckEmitted)
            return;

        var elapsed = DateTime.UtcNow - stuckSince;
        if (elapsed < StuckStateThreshold)
            return;

        int[]? rawSlots = plugin.AddonReader.DumpHandArrayRaw();
        string handDump = rawSlots is null ? "(no raw)" : FormatHandArrayDump(rawSlots);
        int? activeTextureBase = plugin.AddonReader.ActiveLayout?.TileTextureBase;

        plugin.FindingsLog?.Record("stuck_state", new Dictionary<string, object?>
        {
            ["state"] = snap.AddonStateCode,
            ["hand"] = snap.Hand.Count,
            ["melds"] = snap.OurMelds.Count,
            ["legal"] = legal.ToString(),
            ["elapsed_ms"] = (int)elapsed.TotalMilliseconds,
            ["instruction_deadline_ms"] = (int)InstructionDeadline.TotalMilliseconds,
            ["last_dispatch_path"] = plugin.Dispatcher.LastDiscardPath,
            ["last_action"] = LastActionDescription,
            ["hand_raw"] = rawSlots,
            ["tile_texture_base"] = activeTextureBase,
            ["operation_label"] = activeOperation?.Label,
            ["operation_kind"] = activeOperation?.Kind.ToString(),
            ["operation_phase"] = activeOperation?.Phase.ToString(),
            ["selection_sent"] = activeOperation?.SelectionSent,
            ["selection_observed"] = activeOperation?.SelectionObserved,
            ["commit_sent"] = activeOperation?.CommitSent,
            ["commit_observed"] = activeOperation?.CommitObserved,
        });
        string operationNote = activeOperation is { } operation
            ? $" transaction={operation.Label}/{operation.Kind}/{operation.Phase} " +
              $"selection={operation.SelectionSent}/{operation.SelectionObserved} " +
              $"commit={operation.CommitSent}/{operation.CommitObserved}."
            : string.Empty;
        log.Warning(
            $"[AutoPlayLoop] STUCK at state={snap.AddonStateCode} hand={snap.Hand.Count} " +
            $"melds={snap.OurMelds.Count} legal={legal} for {(int)elapsed.TotalSeconds}s " +
            $"(12s instruction deadline; recovery starts at {(int)StuckStateThreshold.TotalSeconds}s). " +
            $"Last dispatch: {LastActionDescription} (path={plugin.Dispatcher.LastDiscardPath})." +
            operationNote + " Manual click required.");
        log.Warning($"[AutoPlayLoop] STUCK hand-array dump: {handDump}");
        stuckEmitted = true;

        // An actionable surface with no transaction is an AI-session stall, not
        // a UI commit that should be clicked again. Recreate the selected engine
        // and bootstrap it from the current live snapshot. Active transactions
        // remain manual-only because restarting an AI cannot prove a click result.
        if (activeOperation is null
            && legal != ActionFlags.None
            && plugin.Configuration.AiProvider != AiProvider.BuiltIn)
        {
            plugin.ForceAiResync($"automatic-stuck state={snap.AddonStateCode} hand={snap.Hand.Count} legal={legal}");
        }
    }

    private string FormatHandArrayDump(int[] slots)
    {
        int textureBase = plugin.AddonReader.ActiveLayout?.TileTextureBase ?? 0;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < slots.Length; i++)
        {
            int raw = slots[i];
            string decoded;
            if (raw == 0)
                decoded = "  -";
            else
            {
                int idx = raw - textureBase;
                decoded = idx switch
                {
                    >= 0 and < 34 => Tile.FromId(idx).ToString(),
                    34 => "5m*",
                    35 => "5p*",
                    36 => "5s*",
                    _ => "??",
                };
            }
            sb.Append($"[{i:00}]={raw,5}({decoded,3})");
            if (i == 6)
                sb.Append(" | ");
            else if (i < slots.Length - 1)
                sb.Append(' ');
        }
        return sb.ToString();
    }

    private void CheckPendingDispatchOutcome(StateSnapshot? snap)
    {
        if (activeOperation is not { } operation || snap is null)
            return;

        var current = BuildOperationSnapshot(snap);
        ActionOperationPhase before = operation.Phase;
        if (!operation.ObserveSnapshot(current))
            return;

        LogOperationTransition(operation, before, operation.Phase, "snapshot");

        if (operation.StructurallyConfirmed)
        {
            if (operation.Kind == ActionOperationKind.OpenCall
                && operation.Choice is { } choice
                && IsCommittedOpenCall(choice))
            {
                CommitAcceptedOpenCall(choice, snap);
            }

            if (operation.Kind == ActionOperationKind.PostCallDiscard)
                pendingPostCallDiscard = null;

            plugin.FindingsLog?.Record("action_transaction_completed", new Dictionary<string, object?>
            {
                ["label"] = operation.Label,
                ["kind"] = operation.Kind.ToString(),
                ["phase"] = operation.Phase.ToString(),
                ["selection_sent"] = operation.SelectionSent,
                ["selection_observed"] = operation.SelectionObserved,
                ["commit_sent"] = operation.CommitSent,
                ["commit_observed"] = operation.CommitObserved,
                ["state_before"] = operation.Baseline.State,
                ["state_after"] = snap.AddonStateCode,
                ["hand_before"] = operation.Baseline.HandCount,
                ["hand_after"] = snap.Hand.Count,
                ["melds_before"] = operation.Baseline.MeldCount,
                ["melds_after"] = snap.OurMelds.Count,
                ["own_discards_before"] = operation.Baseline.OwnDiscardCount,
                ["own_discards_after"] = GetOwnDiscardCount(snap),
                ["elapsed_ms"] = (int)(DateTime.UtcNow - operation.StartedAtUtc).TotalMilliseconds,
            });
            activeOperation = null;
            return;
        }

        if (operation.ClosedWithoutCommit || operation.Cancelled)
        {
            if (operation.Kind == ActionOperationKind.OpenCall
                && operation.Choice is { } choice
                && plugin.Policy is SelectablePolicy selectable)
            {
                selectable.CancelDispatchedOpenCall(choice);
            }
            pendingVariantChoice = null;
            if (operation.Kind == ActionOperationKind.PostCallDiscard)
                pendingPostCallDiscard = null;
            activeOperation = null;
        }
    }

    private ActionOperationSnapshot BuildOperationSnapshot(StateSnapshot snap) => new(
        State: snap.AddonStateCode,
        HandCount: snap.Hand.Count,
        MeldCount: snap.OurMelds.Count,
        OwnDiscardCount: GetOwnDiscardCount(snap),
        TotalDiscardCount: snap.Seats.Sum(seat => seat.DiscardCount),
        WallRemaining: snap.WallRemaining,
        Legal: snap.Legal.Flags,
        OurRiichi: snap.OurRiichi,
        RiichiDiscardSurfaceReady: plugin.Dispatcher.IsRiichiDiscardSelectionSurface());

    private bool BeginOperation(
        string label,
        ActionOperationKind kind,
        StateSnapshot snap,
        ActionChoice? choice = null,
        Tile? tile = null,
        int? selectionOpcode = null,
        int? selectionArgument = null,
        int? commitOpcode = null,
        int? commitArgument = null)
    {
        if (activeOperation is { IsTerminal: false } existing)
        {
            log.Warning(
                "[ActionTransaction] refused overlapping operation new={NewLabel}/{NewKind} active={ActiveLabel}/{ActiveKind}/{Phase}",
                label, kind, existing.Label, existing.Kind, existing.Phase);
            return false;
        }

        activeOperation = new ActionOperationTransaction(
            label, kind, BuildOperationSnapshot(snap), choice, tile,
            selectionOpcode, selectionArgument, commitOpcode, commitArgument);
        log.Information(
            "[ActionTransaction] begin label={Label} kind={Kind} phase={Phase} state={State} hand={Hand} melds={Melds} ownDiscards={Discards}",
            label, kind, activeOperation.Phase, snap.AddonStateCode, snap.Hand.Count, snap.OurMelds.Count, GetOwnDiscardCount(snap));
        return true;
    }

    private void MarkOperationDispatch(
        InputDispatcher.DispatchResult result,
        bool selectionSent,
        bool commitSent)
    {
        if (activeOperation is not { } operation)
            return;

        ActionOperationPhase before = operation.Phase;
        if (selectionSent)
            operation.MarkSelectionSent();
        if (commitSent)
            operation.MarkCommitSent();
        if (result != InputDispatcher.DispatchResult.Ok)
            operation.Cancel();
        LogOperationTransition(operation, before, operation.Phase, "dispatch");
        if (operation.Cancelled)
            activeOperation = null;
    }

    private void MarkDiscardOperationFromPath(InputDispatcher.DispatchResult result)
    {
        string path = plugin.Dispatcher.LastDiscardPath;
        bool selectionSent = path.StartsWith("opcode-15", StringComparison.Ordinal)
            || path.StartsWith("list-widget", StringComparison.Ordinal);
        bool commitSent = path.StartsWith("opcode-15+7", StringComparison.Ordinal)
            || path.StartsWith("opcode-7-fallback", StringComparison.Ordinal)
            || path.StartsWith("list-widget", StringComparison.Ordinal);
        MarkOperationDispatch(result, selectionSent, commitSent);
    }

    private void OnAgentEmjEvent(AgentEmjObservedEvent evt)
    {
        if (activeOperation is not { IsTerminal: false } operation)
            return;
        ActionOperationPhase before = operation.Phase;
        if (!operation.ObserveAgentEvent(evt.Opcode, evt.Argument))
            return;
        LogOperationTransition(operation, before, operation.Phase, $"agent:{evt.Opcode}:{evt.Argument?.ToString() ?? "-"}");
    }

    private void LogOperationTransition(
        ActionOperationTransaction operation,
        ActionOperationPhase before,
        ActionOperationPhase after,
        string source)
    {
        if (before == after)
            return;
        log.Information(
            "[ActionTransaction] {Label}/{Kind} {Before}->{After} source={Source} selection={SelectionSent}/{SelectionObserved} commit={CommitSent}/{CommitObserved}",
            operation.Label, operation.Kind, before, after, source,
            operation.SelectionSent, operation.SelectionObserved,
            operation.CommitSent, operation.CommitObserved);
        plugin.ExecutionTrace.Record("action.transaction.transition", new Dictionary<string, object?>
        {
            ["label"] = operation.Label,
            ["kind"] = operation.Kind.ToString(),
            ["before"] = before.ToString(),
            ["after"] = after.ToString(),
            ["source"] = source,
            ["selection_sent"] = operation.SelectionSent,
            ["selection_observed"] = operation.SelectionObserved,
            ["commit_sent"] = operation.CommitSent,
            ["commit_observed"] = operation.CommitObserved,
        });
    }

    public void ResetForAiResync(string reason)
    {
        // AI-session recovery must never erase an instruction that has already
        // entered the UI transaction. The game-side selection/commit remains
        // authoritative and can finish independently of the recreated AI process.
        if (activeOperation is { IsTerminal: false } operation)
        {
            log.Warning(
                "[AI再同期] preserving active instruction label={Label} kind={Kind} phase={Phase} reason={Reason}",
                operation.Label, operation.Kind, operation.Phase, reason);
        }
        else
        {
            ResetOperationState(
                $"ai-resync:{reason}",
                preservePostCallDiscard: true,
                preserveVariantChoice: true);
        }

        stuckStateCode = null;
        stuckHandCount = null;
        stuckLegal = null;
        stuckEmitted = false;
    }

    private void ResetOperationState(
        string reason,
        bool preservePostCallDiscard = false,
        bool preserveVariantChoice = false)
    {
        if (activeOperation is { } operation)
            log.Information("[ActionTransaction] reset label={Label} kind={Kind} phase={Phase} reason={Reason}", operation.Label, operation.Kind, operation.Phase, reason);
        activeOperation?.Cancel();
        activeOperation = null;
        if (!preservePostCallDiscard)
            pendingPostCallDiscard = null;
        if (!preserveVariantChoice)
            pendingVariantChoice = null;
        fsm.Reset();
    }

    internal static bool IsCommittedOpenCall(ActionChoice? choice) =>
        choice is { Call: not null }
        && choice.Kind is ActionKind.Pon or ActionKind.Chi or ActionKind.MinKan;

    private void CommitAcceptedOpenCall(ActionChoice choice, StateSnapshot committedState)
    {
        if (choice.Call is not { } candidate)
            return;

        int absoluteFromSeat = candidate.FromSeat < 0
            ? -1
            : (Math.Clamp(committedState.OurSeat, 0, 3) + Math.Clamp(candidate.FromSeat, 0, 3)) & 3;
        MeldCandidate absoluteCandidate = candidate with { FromSeat = absoluteFromSeat };
        Meld exactMeld = Meld.FromAcceptedCandidate(absoluteCandidate);
        int meldsBefore = activeOperation?.Baseline.MeldCount ?? committedState.OurMelds.Count;
        bool trackerAlreadyAdvanced = committedState.OurMelds.Count > meldsBefore;
        bool alreadyTracked = committedState.OurMelds.Any(existing => MeldEquivalent(existing, exactMeld));
        if (!trackerAlreadyAdvanced && !alreadyTracked)
            plugin.MeldTracker.Record(exactMeld);

        if (plugin.Policy is SelectablePolicy selectable)
            selectable.NotifyCommittedAction(choice, committedState);

        if (choice.Kind is ActionKind.Chi or ActionKind.Pon
            && choice.PostCallDiscardTile is { } postCallTile)
        {
            pendingPostCallDiscard = new PendingPostCallDiscard(
                postCallTile,
                choice.Kind,
                GetOwnDiscardCount(committedState),
                DispatchSent: false);
            log.Information(
                "[AutoPlayLoop] retained Akochan post-call discard kind={Kind} tile={Tile} ownDiscards={DiscardCount}",
                choice.Kind,
                postCallTile,
                pendingPostCallDiscard.Value.OwnDiscardCountAtCommit);
        }

        pendingVariantChoice = null;
        fsm.ClearContext();
        LastActionDescription = choice.PostCallDiscardTile is { } exactDiscard
            ? $"{choice.Kind}成立を確認。Akochan確定打牌 {exactDiscard} へ移行"
            : $"{choice.Kind}成立を確認。鳴き後の打牌判断へ移行";
        log.Information(
            "[AutoPlayLoop] committed call kind={Kind} hand={HandBefore}->{HandAfter} meldTracked={Tracked}; stale call surface released",
            choice.Kind,
            activeOperation?.Baseline.HandCount ?? -1,
            committedState.Hand.Count,
            trackerAlreadyAdvanced || alreadyTracked);
        plugin.ExecutionTrace.Record("call.commit.confirmed", new Dictionary<string, object?>
        {
            ["kind"] = choice.Kind.ToString(),
            ["claimed"] = candidate.ClaimedTile.ToString(),
            ["consumed"] = string.Join(',', candidate.HandTiles.Select(tile => tile.ToString())),
            ["hand"] = committedState.Hand.Count,
            ["melds"] = committedState.OurMelds.Count,
            ["state"] = committedState.AddonStateCode,
            ["legal"] = committedState.Legal.Flags.ToString(),
            ["already_tracked"] = trackerAlreadyAdvanced || alreadyTracked,
            ["post_call_discard"] = choice.PostCallDiscardTile?.ToString(),
        });
    }

    private int GetOwnDiscardCount(StateSnapshot state)
    {
        if (state.Seats.Count == 0)
            return 0;
        int seat = Math.Clamp(state.OurSeat, 0, state.Seats.Count - 1);
        return state.Seats[seat].DiscardCount;
    }

    private bool TryDispatchPendingPostCallDiscard(StateSnapshot snap)
    {
        if (pendingPostCallDiscard is not { } pending)
            return false;

        int ownDiscardCount = GetOwnDiscardCount(snap);
        if (ownDiscardCount > pending.OwnDiscardCountAtCommit)
        {
            log.Information(
                "[AutoPlayLoop] post-call discard structurally confirmed; clearing retained tile={Tile}",
                pending.Tile);
            pendingPostCallDiscard = null;
            return false;
        }

        if (pending.DispatchSent)
        {
            LastActionDescription =
                $"waiting for post-call discard structural commit ({pending.CallKind} {pending.Tile})";
            return true;
        }

        if (!snap.Hand.Contains(pending.Tile))
        {
            log.Warning(
                "[AutoPlayLoop] retained post-call discard tile disappeared before dispatch: kind={Kind} tile={Tile} hand={Hand}",
                pending.CallKind,
                pending.Tile,
                string.Join(',', snap.Hand.Select(tile => tile.ToString())));
            pendingPostCallDiscard = null;
            return false;
        }

        if (!snap.Legal.Can(ActionFlags.Discard))
            return false;

        // A committed Chi/Pon leaves 11/8/5/2 concealed tiles before the
        // mandatory discard. The retained action is bound to that exact shape,
        // so it cannot leak into an unrelated later self-draw.
        if (snap.Hand.Count is not (11 or 8 or 5 or 2))
            return false;

        int slot = plugin.AddonReader.FindAddonSlotOfTile(pending.Tile);
        if (slot < 0)
        {
            log.Warning(
                "[AutoPlayLoop] retained post-call discard tile not found in addon slots: kind={Kind} tile={Tile}",
                pending.CallKind,
                pending.Tile);
            pendingPostCallDiscard = null;
            return false;
        }

        if (!BeginOperation(
                "post-call-discard",
                ActionOperationKind.PostCallDiscard,
                snap,
                tile: pending.Tile,
                selectionOpcode: 15,
                commitOpcode: 7,
                commitArgument: slot))
            return true;

        var context = new DispatchContext(
            NormalizeDispatchState(snap.AddonStateCode, isDiscardTurn: true),
            snap.Hand.Count);
        fsm.BeginDispatch(DateTime.UtcNow, context);
        try
        {
            var result = plugin.Dispatcher.DispatchDiscard(slot);
            MarkDiscardOperationFromPath(result);
            LastActionDescription =
                $"auto-post-call-discard {pending.Tile} slot={slot} ({pending.CallKind}) → {result}";
            log.Information("[AutoPlayLoop] {Description}", LastActionDescription);
            plugin.GameLogger.RecordAction(
                ActionKind.Discard,
                pending.Tile,
                slot,
                result.ToString(),
                $"Akochan {pending.CallKind} response follow-up");
            EmitDispatchFinding(
                "post-call-discard",
                result,
                tile: pending.Tile,
                slot: slot,
                snap: snap);
            pendingPostCallDiscard = result == InputDispatcher.DispatchResult.Ok
                ? pending with { DispatchSent = true }
                : null;
        }
        finally
        {
            fsm.CompleteDispatch();
        }

        return true;
    }

    private static bool MeldEquivalent(Meld left, Meld right)
    {
        if (left.Kind != right.Kind
            || left.ClaimedFromSeat != right.ClaimedFromSeat
            || left.ClaimedTile != right.ClaimedTile
            || left.Tiles.Length != right.Tiles.Length)
            return false;

        int[] leftTiles = left.Tiles.Select(tile => (int)tile.Id).OrderBy(id => id).ToArray();
        int[] rightTiles = right.Tiles.Select(tile => (int)tile.Id).OrderBy(id => id).ToArray();
        return leftTiles.SequenceEqual(rightTiles);
    }

    private bool IsAutomationLoopEnabled()
    {
        var cfg = plugin.Configuration;
        plugin.ExecutionTrace.Record("autoplay.gates", new Dictionary<string, object?>
        {
            ["tos"] = cfg.TosAccepted, ["armed"] = cfg.AutomationArmed, ["suggestion_only"] = cfg.SuggestionOnly,
            ["auto_call"] = cfg.AutoCallEnabled, ["auto_advance"] = cfg.AutoAdvanceAfterHand,
        });
        if (!cfg.TosAccepted || !cfg.AutomationArmed)
            return false;

        // Hints mode still runs the prompt handler when automatic calls are
        // explicitly enabled. Discard, riichi, win and hand-advance automation
        // remain blocked by the callOnlyMode checks in OnUpdate.
        return !cfg.SuggestionOnly || cfg.AutoCallEnabled;
    }

    private bool ContinueAfterStuckRecovery()
    {
        if (!fsm.IsDispatchInFlight)
            return true;
        if (fsm.TryRecoverFromStuckDispatch(DateTime.UtcNow))
        {
            log.Warning("[AutoPlayLoop] resetting stuck actionPending");
            return true;
        }
        return false;
    }

    private void HandleCallVariantSelect(DispatchContext context)
    {
        if (fsm.ShouldSuppressForContext(context, DateTime.UtcNow))
            return;
        ScheduleVariantAccept(context);
    }

    private bool TryHandleHandResult(int state)
    {
        if (state != HandResultStateCode)
        {
            handResultFirstSeenAt = null;
            handResultDispatchedThisInstance = false;
            return false;
        }

        LastObservedState = state;
        LastObservedHandCount = -1;

        var now = DateTime.UtcNow;
        handResultFirstSeenAt ??= now;

        if (handResultDispatchedThisInstance)
            return true;
        if (now - handResultFirstSeenAt < HandResultStabilityWindow)
            return true;

        var context = new DispatchContext(state, -1);
        if (fsm.ShouldSuppressForContext(context, now))
            return true;

        EmitProgressing();
        handResultDispatchedThisInstance = true;
        ScheduleHandResultAdvance(context);
        return true;
    }

    /// <summary>
    /// Completes a Riichi declaration exactly once. The first dispatch selects
    /// Riichi. EMJ then changes the same state-6 list into a discard-candidate
    /// surface. Only that structural AtkValue transition authorizes the selected
    /// discard; a still-visible Riichi/Pass list is treated as pending, not as a
    /// reason to click Riichi again.
    /// </summary>
    private bool TryHandleRiichiConfirmTsumogiri(StateSnapshot snap, DispatchContext context)
    {
        if (!fsm.IsRiichiConfirmPending)
            return false;

        if (snap.OurRiichi)
        {
            fsm.ClearRiichiConfirm();
            return false;
        }

        // A hand-count change means the declaration flow already ended or the
        // surface no longer belongs to the latched decision. Drop it rather than
        // carrying a stale click into a later prompt.
        if (context.Hand != 14)
        {
            fsm.ClearRiichiConfirm();
            return false;
        }

        if (!plugin.Dispatcher.IsRiichiDiscardSelectionSurface())
        {
            LastActionDescription = "auto-riichi: waiting for discard-candidate surface";
            return true;
        }

        ScheduleRiichiTsumogiri(context);
        return true;
    }

    private void ScheduleAction(string label, DispatchContext context, int medianDelayMs, Action body)
        => ScheduleAction(label, context, HumanTiming.RandomDelay(medianMs: medianDelayMs), body);

    private void ScheduleAction(string label, DispatchContext context, TimeSpan delay, Action body)
    {
        fsm.BeginDispatch(DateTime.UtcNow, context);
        log.Info($"[AutoPlayLoop] scheduled {label} after {delay.TotalMilliseconds:F0} ms");
        _ = framework.RunOnTick(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                log.Error($"AutoPlayLoop {label} error: {ex}");
                LastActionDescription = $"{label} exception: {ex.Message}";
            }
            finally
            {
                fsm.CompleteDispatch();
            }
        }, delay);
    }

    private TimeSpan ResolveHumanDelay(ActionChoice choice, StateSnapshot snap, DispatchContext context)
    {
        var cfg = plugin.Configuration;
        plugin.ExecutionTrace.Record("autoplay.gates", new Dictionary<string, object?>
        {
            ["tos"] = cfg.TosAccepted, ["armed"] = cfg.AutomationArmed, ["suggestion_only"] = cfg.SuggestionOnly,
            ["auto_call"] = cfg.AutoCallEnabled, ["auto_advance"] = cfg.AutoAdvanceAfterHand,
        });
        if (!cfg.HumanDelayEnabled)
            return TimeSpan.Zero;

        int min;
        int max;
        bool tsumogiri = choice.Kind == ActionKind.Discard
            && choice.DiscardTile is { } tile
            && plugin.AddonReader.FindAddonSlotOfTile(tile) == 13;

        if (choice.Kind is ActionKind.Ron or ActionKind.Tsumo)
            (min, max) = (cfg.WinDelayMinMs, cfg.WinDelayMaxMs);
        else if (choice.Kind == ActionKind.Riichi)
            (min, max) = (cfg.RiichiDelayMinMs, cfg.RiichiDelayMaxMs);
        else if (choice.Kind == ActionKind.Pass)
            (min, max) = (cfg.PassDelayMinMs, cfg.PassDelayMaxMs);
        else if (choice.Kind == ActionKind.Pon)
            (min, max) = (cfg.PonDelayMinMs, cfg.PonDelayMaxMs);
        else if (choice.Kind == ActionKind.Chi)
            (min, max) = (cfg.ChiDelayMinMs, cfg.ChiDelayMaxMs);
        else if (choice.Kind == ActionKind.MinKan)
            (min, max) = (cfg.MinKanDelayMinMs, cfg.MinKanDelayMaxMs);
        else if (choice.Kind == ActionKind.AnKan)
            (min, max) = (cfg.AnKanDelayMinMs, cfg.AnKanDelayMaxMs);
        else if (choice.Kind == ActionKind.ShouMinKan)
            (min, max) = (cfg.ShouMinKanDelayMinMs, cfg.ShouMinKanDelayMaxMs);
        else if (tsumogiri)
            (min, max) = (cfg.TsumogiriDelayMinMs, cfg.TsumogiriDelayMaxMs);
        else
            (min, max) = (cfg.DiscardDelayMinMs, cfg.DiscardDelayMaxMs);

        double elapsedMs = timingContext.HasValue && timingContext.Value.Equals(context)
            ? (DateTime.UtcNow - timingContextFirstSeenUtc).TotalMilliseconds
            : 0;
        int remainingMs = Math.Max(0, cfg.TurnTimeBudgetMs - (int)elapsedMs);
        if (remainingMs <= cfg.EmergencyImmediateThresholdMs)
            return TimeSpan.Zero;
        if (remainingMs <= 6000)
            return HumanTiming.RandomRange(500, 1000);
        if (remainingMs <= 10000)
            return HumanTiming.RandomRange(1500, 2500);

        // The configured delay is measured from when the actionable surface first
        // appeared, not from when the AI finally returned. AI inference and the
        // human-like delay therefore run concurrently instead of stacking.
        // Example: a 1,000 ms Pass delay with an 850 ms AI inference waits only
        // the remaining ~150 ms after the decision arrives.
        max = Math.Min(max, Math.Max(0, remainingMs - 1000));
        min = Math.Min(min, max);
        int targetDelayMs = (int)HumanTiming.RandomRange(min, max).TotalMilliseconds;
        int delayStillRequiredMs = Math.Max(0, targetDelayMs - (int)elapsedMs);
        return TimeSpan.FromMilliseconds(delayStillRequiredMs);
    }

    private static string ActionFingerprint(StateSnapshot snap, ActionChoice choice) =>
        // AddonStateCode is intentionally excluded: state 6/22/30 can describe
        // the same unchanged discard surface while the UI animates.
        string.Join('|', snap.WallRemaining, (int)snap.Legal.Flags,
            string.Join(',', snap.Hand.Select(t => t.Id)), choice.Kind, choice.DiscardTile?.Id ?? -1,
            choice.Call?.Kind.ToString() ?? "-",
            choice.Call is { } call ? string.Join(',', call.HandTiles.Select(t => t.Id)) : "-",
            choice.CallConsumedRed.Count > 0 ? string.Join(',', choice.CallConsumedRed.Select(red => red ? 1 : 0)) : "-",
            choice.PostCallDiscardTile?.Id ?? -1);

    private bool IsDuplicateAction(string fingerprint)
    {
        DateTime now = DateTime.UtcNow;
        if (string.Equals(lastActionFingerprint, fingerprint, StringComparison.Ordinal)
            && now - lastActionFingerprintAtUtc < TimeSpan.FromSeconds(10))
            return true;
        lastActionFingerprint = fingerprint;
        lastActionFingerprintAtUtc = now;
        return false;
    }

    internal static bool IsAutomaticCallAllowed(Configuration cfg, ActionKind kind)
    {
        if (!cfg.AutoCallEnabled)
            return false;

        return kind switch
        {
            ActionKind.Pass => cfg.AutoPassEnabled,
            ActionKind.Pon => cfg.AutoPonEnabled,
            ActionKind.Chi => cfg.AutoChiEnabled,
            ActionKind.AnKan => cfg.AutoAnKanEnabled,
            ActionKind.MinKan => cfg.AutoMinKanEnabled,
            ActionKind.ShouMinKan => cfg.AutoShouMinKanEnabled,
            _ => true,
        };
    }

    /// <summary>
    /// Hint mode with automatic calls enabled: AutoPlayLoop owns call/self-kan
    /// prompts only and must never execute ordinary discards from those surfaces.
    /// </summary>
    internal static bool IsCallOnlyAutomation(Configuration cfg) =>
        cfg.TosAccepted
        && cfg.AutomationArmed
        && cfg.SuggestionOnly
        && cfg.AutoCallEnabled;

    internal static ActionChoice ResolveDisabledCallChoice(
        Configuration cfg,
        StateSnapshot snap,
        ActionKind disabledKind,
        string reason,
        Func<StateSnapshot, ActionChoice>? chooseBuiltIn)
    {
        // Opponent-discard prompts and self-declare lists both expose Pass.
        // Hint+call-only automation must honour pass-only settings instead of
        // silently auto-discarding from an AnKan/Kan popup.
        if (snap.Legal.Can(ActionFlags.Pass)
            && cfg.AutoPassEnabled
            && (!snap.Legal.Can(ActionFlags.Discard) || IsCallOnlyAutomation(cfg)))
            return ActionChoice.Pass(reason);

        // Full autoplay may still fall back to a built-in discard when a disabled
        // self-kan is declined and no Pass automation is configured.
        if (snap.Legal.Can(ActionFlags.Discard) && !IsCallOnlyAutomation(cfg))
        {
            var filtered = FilterDisabledCall(snap, disabledKind);
            if (chooseBuiltIn is not null)
            {
                var fallback = chooseBuiltIn(filtered);
                if (fallback.Kind is ActionKind.Discard or ActionKind.Riichi
                    && fallback.DiscardTile is { } tile
                    && filtered.Hand.Contains(tile))
                {
                    return fallback with { Reasoning = $"{reason}; {fallback.Reasoning}" };
                }
            }

            if (filtered.Legal.DiscardableTiles.Count > 0)
            {
                Tile discard = filtered.Legal.DiscardableTiles[0];
                if (filtered.Hand.Contains(discard))
                    return ActionChoice.Discard(discard, $"{reason}; first legal discard fallback");
            }
        }

        return ActionChoice.Pass(reason);
    }

    private ActionChoice ApplyAutomaticCallSwitches(StateSnapshot snap, ActionChoice choice)
    {
        if (choice.Kind is not (ActionKind.Pon or ActionKind.Chi or ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan))
            return choice;

        var cfg = plugin.Configuration;
        plugin.ExecutionTrace.Record("autoplay.gates", new Dictionary<string, object?>
        {
            ["tos"] = cfg.TosAccepted, ["armed"] = cfg.AutomationArmed, ["suggestion_only"] = cfg.SuggestionOnly,
            ["auto_call"] = cfg.AutoCallEnabled, ["auto_advance"] = cfg.AutoAdvanceAfterHand,
        });
        if (IsAutomaticCallAllowed(cfg, choice.Kind))
            return choice;

        string reason = $"auto-{choice.Kind} disabled by settings";
        log.Info("[AutoPlayLoop] {Reason}; preserving AI hint and blocking automatic acceptance", reason);

        Func<StateSnapshot, ActionChoice>? chooseBuiltIn = plugin.Policy is SelectablePolicy selectable
            ? selectable.ChooseBuiltIn
            : null;
        return ResolveDisabledCallChoice(cfg, snap, choice.Kind, reason, chooseBuiltIn);
    }

    private bool IsAutomaticPromptActionAllowed(ActionChoice choice)
    {
        var cfg = plugin.Configuration;
        if (IsCallOnlyAutomation(cfg) && choice.Kind is ActionKind.Discard or ActionKind.Riichi)
            return false;

        if (choice.Kind is not (ActionKind.Pass or ActionKind.Pon or ActionKind.Chi or ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan))
            return true;

        return IsAutomaticCallAllowed(cfg, choice.Kind);
    }

    private static StateSnapshot FilterDisabledCall(StateSnapshot snap, ActionKind kind)
    {
        ActionFlags removed = kind switch
        {
            ActionKind.Pon => ActionFlags.Pon,
            ActionKind.Chi => ActionFlags.Chi,
            ActionKind.AnKan => ActionFlags.AnKan,
            ActionKind.MinKan => ActionFlags.MinKan,
            ActionKind.ShouMinKan => ActionFlags.ShouMinKan,
            _ => ActionFlags.None,
        };

        var legal = snap.Legal with
        {
            Flags = snap.Legal.Flags & ~removed,
            PonCandidates = kind == ActionKind.Pon ? [] : snap.Legal.PonCandidates,
            ChiCandidates = kind == ActionKind.Chi ? [] : snap.Legal.ChiCandidates,
            KanCandidates = kind is ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan
                ? snap.Legal.KanCandidates.Where(c => !MatchesAction(c.Kind, kind)).ToArray()
                : snap.Legal.KanCandidates,
        };
        return snap with { Legal = legal };
    }

    private static bool MatchesAction(MeldKind meldKind, ActionKind actionKind) => (meldKind, actionKind) switch
    {
        (MeldKind.AnKan, ActionKind.AnKan) => true,
        (MeldKind.MinKan, ActionKind.MinKan) => true,
        (MeldKind.ShouMinKan, ActionKind.ShouMinKan) => true,
        _ => false,
    };

    internal static bool IsChoiceStillLegal(StateSnapshot snap, ActionChoice choice)
    {
        if (choice.Kind is ActionKind.Discard or ActionKind.Riichi)
            return choice.DiscardTile is { } tile && snap.Hand.Contains(tile);
        return choice.Kind switch
        {
            ActionKind.Pon => snap.Legal.Can(ActionFlags.Pon),
            ActionKind.Chi => snap.Legal.Can(ActionFlags.Chi),
            ActionKind.AnKan => snap.Legal.Can(ActionFlags.AnKan),
            ActionKind.MinKan => snap.Legal.Can(ActionFlags.MinKan),
            ActionKind.ShouMinKan => snap.Legal.Can(ActionFlags.ShouMinKan),
            ActionKind.Ron => snap.Legal.Can(ActionFlags.Ron),
            ActionKind.Tsumo => snap.Legal.Can(ActionFlags.Tsumo),
            ActionKind.Pass => snap.Legal.Can(ActionFlags.Pass),
            _ => false,
        };
    }

    private void ScheduleHandResultAdvance(DispatchContext context)
    {
        ScheduleAction("hand-result-next", context, HandResultAdvanceDelayMs, () =>
        {
            int currentState = ReadStateCode();
            if (currentState != HandResultStateCode)
            {
                LastActionDescription = $"hand-result-next aborted: state moved {HandResultStateCode}→{currentState}";
                return;
            }

            var snap = plugin.AddonReader.TryBuildSnapshot();
        plugin.ExecutionTrace.Record("autoplay.snapshot", snap is null ? new Dictionary<string, object?> { ["available"] = false } : new Dictionary<string, object?>
        {
            ["available"] = true, ["state"] = snap.AddonStateCode, ["hand"] = snap.Hand.Count, ["wall"] = snap.WallRemaining,
            ["legal"] = snap.Legal.Flags.ToString(), ["discardable"] = string.Join(",", snap.Legal.DiscardableTiles),
            ["pon_count"] = snap.Legal.PonCandidates.Count, ["chi_count"] = snap.Legal.ChiCandidates.Count, ["kan_count"] = snap.Legal.KanCandidates.Count,
            ["our_melds"] = snap.OurMelds.Count, ["turn"] = snap.TurnIndex,
        });
            var result = plugin.Dispatcher.DispatchHandResultNext();
            LastActionDescription = $"auto-hand-result-next → {result}";
            log.Info($"[AutoPlayLoop] hand-result-next dispatch: {LastActionDescription}");
            plugin.GameLogger.RecordAction(ActionKind.Pass, null, null, result.ToString(), "auto-advance after hand");
            EmitDispatchFinding("hand-result-next", result, state: currentState, snap: snap);
            ClearRetryDebounceIfHookFailed(result);
        });
    }

    private void ScheduleVariantAccept(DispatchContext context)
    {
        ScheduleAction("call-pattern", context, VariantAcceptDelayMs, () =>
        {
            // Modal can close during the humanized delay — re-check at dispatch time.
            int currentState = ReadStateCode();
            if (currentState != CallVariantSelectStateCode)
            {
                LastActionDescription = $"variant aborted: state moved {CallVariantSelectStateCode}→{currentState}";
                return;
            }

            int bestIdx = 0;
            string scoreNote = "default(opt=0)";
            var variants = TryReadCallVariants();
            var snap = plugin.AddonReader.TryBuildSnapshot();
        plugin.ExecutionTrace.Record("autoplay.snapshot", snap is null ? new Dictionary<string, object?> { ["available"] = false } : new Dictionary<string, object?>
        {
            ["available"] = true, ["state"] = snap.AddonStateCode, ["hand"] = snap.Hand.Count, ["wall"] = snap.WallRemaining,
            ["legal"] = snap.Legal.Flags.ToString(), ["discardable"] = string.Join(",", snap.Legal.DiscardableTiles),
            ["pon_count"] = snap.Legal.PonCandidates.Count, ["chi_count"] = snap.Legal.ChiCandidates.Count, ["kan_count"] = snap.Legal.KanCandidates.Count,
            ["our_melds"] = snap.OurMelds.Count, ["turn"] = snap.TurnIndex,
        });
            if (variants is { Count: > 0 } && pendingVariantChoice is { Call: { } selected } selectedChoice
                && TryFindChosenCallVariant(variants, selectedChoice, out int selectedIdx))
            {
                bestIdx = selectedIdx;
                scoreNote = $"AI-selected call pattern {selectedIdx}";
            }
            else if (variants is { Count: > 1 } && snap is not null)
            {
                if (pendingVariantChoice?.Kind == ActionKind.Chi)
                    bestIdx = PickBestChiVariantIndex(variants, snap, out scoreNote);
                else
                    bestIdx = PickRedPreservingVariantIndex(variants, out scoreNote);
            }

            ActionChoice? committedVariantChoice = pendingVariantChoice;
            ActionKind variantKind = committedVariantChoice?.Kind ?? ActionKind.Chi;
            if (activeOperation is { Kind: ActionOperationKind.OpenCall } callOperation)
                callOperation.SetExpectedCommit(12, bestIdx);
            else if (snap is not null && committedVariantChoice is { } variantChoice)
                BeginOperation(
                    "call-pattern",
                    ActionOperationKind.OpenCall,
                    snap,
                    variantChoice,
                    variantChoice.Call?.ClaimedTile,
                    commitOpcode: 12,
                    commitArgument: bestIdx);
            var result = plugin.Dispatcher.DispatchCallVariant(bestIdx);
            MarkOperationDispatch(result, selectionSent: false, commitSent: true);
            pendingVariantChoice = null;
            LastActionDescription = $"auto-call-pattern[opt={bestIdx}] → {result} ({scoreNote})";
            log.Info($"[AutoPlayLoop] call-pattern dispatch: {LastActionDescription}");
            plugin.GameLogger.RecordAction(variantKind, null, bestIdx, result.ToString(), $"call-pattern: {scoreNote}");
            EmitDispatchFinding(
                "call-pattern", result, option: bestIdx, state: currentState, snap: snap,
                committedChoice: committedVariantChoice);
        });
    }

    /// <summary>Reads the three-tile meld patterns from the verified state-25 call-pattern popup. Capture 2026-05-25: atk[3]=variant_count, then 4 ints per pattern (3 tile textures + 1 sentinel).</summary>
    private unsafe IReadOnlyList<CallVariantOption>? TryReadCallVariants()
    {
        if (!addon.TryGet(out var unit, out _))
            return null;
        if (!unit->IsVisible || unit->AtkValues == null)
            return null;

        int atkCount = unit->AtkValuesCount;
        if (atkCount < 4)
            return null;

        var atk = unit->AtkValues;
        if (atk[3].Type != AtkValueType.Int)
            return null;
        int variantCount = atk[3].Int;
        if (variantCount is < 1 or > 8)
            return null;

        int textureBase = plugin.AddonReader.ActiveLayout?.TileTextureBase ?? 0;
        if (textureBase == 0)
            return null;

        int needed = 4 + variantCount * 4;
        if (atkCount < needed)
            return null;

        var variants = new List<CallVariantOption>(variantCount);
        for (int i = 0; i < variantCount; i++)
        {
            int baseIdx = 4 + i * 4;
            var tiles = new VariantTile[3];
            for (int j = 0; j < 3; j++)
            {
                if (atk[baseIdx + j].Type != AtkValueType.Int)
                    return null;
                int id = HandArrayDecoder.DecodeTileId(atk[baseIdx + j].Int, textureBase, out bool isRed);
                if (id < 0)
                    return null;
                tiles[j] = new VariantTile(id, isRed);
            }
            variants.Add(new CallVariantOption(tiles));
        }
        return variants;
    }

    private readonly record struct VariantTile(int Id, bool IsRed);
    private sealed record CallVariantOption(VariantTile[] Tiles);

    internal static int FindCallPatternIndexForTest(
        IReadOnlyList<(int Id, bool IsRed)[]> variants,
        ActionChoice selectedChoice)
    {
        CallVariantOption[] converted = variants
            .Select(pattern => new CallVariantOption(
                pattern.Select(tile => new VariantTile(tile.Id, tile.IsRed)).ToArray()))
            .ToArray();
        return TryFindChosenCallVariant(converted, selectedChoice, out int index) ? index : -1;
    }

    private static bool TryFindChosenCallVariant(
        IReadOnlyList<CallVariantOption> variants,
        ActionChoice selectedChoice,
        out int index)
    {
        if (selectedChoice.Call is not { } selected)
        {
            index = -1;
            return false;
        }

        int[] wantedIds = selected.HandTiles.Select(t => (int)t.Id).OrderBy(id => id).ToArray();
        bool[] wantedRed = selectedChoice.CallConsumedRed.Count == selected.HandTiles.Length
            ? selectedChoice.CallConsumedRed.ToArray()
            : [];

        for (int i = 0; i < variants.Count; i++)
        {
            VariantTile[] tiles = variants[i].Tiles;
            for (int claimedIndex = 0; claimedIndex < tiles.Length; claimedIndex++)
            {
                if (tiles[claimedIndex].Id != selected.ClaimedTile.Id)
                    continue;

                VariantTile[] consumed = tiles.Where((_, idx) => idx != claimedIndex).ToArray();
                if (!consumed.Select(t => t.Id).OrderBy(id => id).SequenceEqual(wantedIds))
                    continue;

                if (wantedRed.Length > 0)
                {
                    var actual = consumed.Select(t => (t.Id, t.IsRed)).OrderBy(t => t.Id).ThenBy(t => t.IsRed).ToArray();
                    var expected = selected.HandTiles
                        .Select((tile, idx) => (Id: (int)tile.Id, IsRed: wantedRed[idx]))
                        .OrderBy(t => t.Id).ThenBy(t => t.IsRed).ToArray();
                    if (!actual.SequenceEqual(expected))
                        continue;
                }

                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    /// <summary>Picks the chi variant whose post-call closed hand has the lowest shanten. Tries all 3 (claim, hand-pair) splits per variant since the claimed tile isn't explicitly marked in AtkValues. Ties resolve to the lower variant index.</summary>
    private static int PickBestChiVariantIndex(IReadOnlyList<CallVariantOption> variants, StateSnapshot snap, out string note)
    {
        var counts = new int[Mahjong.Core.Tile.Count34];
        foreach (var t in snap.Hand)
            counts[t.Id]++;
        int meldsAfter = snap.OurMelds.Count + 1;

        int bestIdx = 0;
        int bestShanten = int.MaxValue;
        for (int v = 0; v < variants.Count; v++)
        {
            var tiles = variants[v].Tiles;
            int? variantShanten = null;
            for (int claimSlot = 0; claimSlot < 3; claimSlot++)
            {
                int h1 = tiles[(claimSlot + 1) % 3].Id;
                int h2 = tiles[(claimSlot + 2) % 3].Id;
                if (counts[h1] < 1) continue;
                counts[h1]--;
                if (counts[h2] < 1) { counts[h1]++; continue; }
                counts[h2]--;
                int sh = Mahjong.Engine.ShantenCalculator.Standard(counts, meldsAfter);
                counts[h1]++;
                counts[h2]++;
                if (variantShanten is null || sh < variantShanten)
                    variantShanten = sh;
            }
            if (variantShanten is null) continue;
            if (variantShanten < bestShanten)
            {
                bestShanten = variantShanten.Value;
                bestIdx = v;
            }
        }

        note = bestShanten == int.MaxValue
            ? $"no formable variant, default(opt=0) of {variants.Count}"
            : $"shanten={bestShanten} across {variants.Count} variants";
        return bestIdx;
    }

    private bool HasAuthoritativeSelectedAiDecision()
    {
        if (plugin.Configuration.AiProvider == AiProvider.BuiltIn)
            return true;
        if (plugin.Policy is not SelectablePolicy selectable)
            return false;

        return plugin.Configuration.AiProvider switch
        {
            AiProvider.BundledMortal => selectable.LastDecisionSource == "Mortal",
            AiProvider.BundledAkochan => selectable.LastDecisionSource == "Akochan",
            AiProvider.ExternalMjai => selectable.LastDecisionSource == "外部MJAI",
            _ => false,
        };
    }

    private void ScheduleDiscard(DispatchContext context)
    {
        var initial = plugin.AddonReader.TryBuildSnapshot();
        if (initial is null || !initial.Legal.Can(ActionFlags.Discard))
            return;

        var choice = plugin.Policy.Choose(initial);
        EmitDiagnosticDecision("discard", initial, choice);
        if (Mahjong.Plugin.Dalamud.ExternalAi.SelectablePolicy.IsPendingChoice(choice))
        {
            LastActionDescription = choice.Reasoning;
            fsm.ClearContext();
            return;
        }

        if (!HasAuthoritativeSelectedAiDecision())
        {
            string source = plugin.Policy is SelectablePolicy selectable
                ? selectable.LastDecisionSource
                : "不明";
            LastActionDescription = $"waiting for selected AI discard instruction (source={source})";
            log.Info("[AutoPlayLoop] {Description}", LastActionDescription);
            fsm.ClearContext();
            return;
        }

        choice = ApplyAutomaticCallSwitches(initial, choice);
        if (choice.Kind is not (ActionKind.Discard or ActionKind.Riichi)
            || !IsChoiceStillLegal(initial, choice))
        {
            LastActionDescription = $"discard decision not ready: {choice.Kind} legal={initial.Legal.Flags}";
            log.Info("[AutoPlayLoop] {Description}", LastActionDescription);
            fsm.ClearContext();
            return;
        }

        string fingerprint = ActionFingerprint(initial, choice);
        TimeSpan delay = ResolveHumanDelay(choice, initial, context);
        ScheduleAction("discard", context, delay, () =>
        {
            var snap = plugin.AddonReader.TryBuildSnapshot();
        plugin.ExecutionTrace.Record("autoplay.snapshot", snap is null ? new Dictionary<string, object?> { ["available"] = false } : new Dictionary<string, object?>
        {
            ["available"] = true, ["state"] = snap.AddonStateCode, ["hand"] = snap.Hand.Count, ["wall"] = snap.WallRemaining,
            ["legal"] = snap.Legal.Flags.ToString(), ["discardable"] = string.Join(",", snap.Legal.DiscardableTiles),
            ["pon_count"] = snap.Legal.PonCandidates.Count, ["chi_count"] = snap.Legal.ChiCandidates.Count, ["kan_count"] = snap.Legal.KanCandidates.Count,
            ["our_melds"] = snap.OurMelds.Count, ["turn"] = snap.TurnIndex,
        });
            int currentState = ReadStateCode();
            if (snap is null || !snap.Legal.Can(ActionFlags.Discard) || !IsChoiceStillLegal(snap, choice))
            {
                LastActionDescription = $"discard aborted: stale or illegal decision (state={currentState})";
                log.Info($"[AutoPlayLoop] {LastActionDescription}");
                fsm.ClearContext();
                return;
            }
            if (IsDuplicateAction(fingerprint))
            {
                // A duplicate fingerprint is not sufficient proof that the previous
                // dispatch succeeded. EMJ can acknowledge a callback while leaving the
                // same 14-tile Discard surface active (often after a stale call prompt).
                // The live legality/hand checks above are the authoritative guard: when
                // the tile is still present and Discard is still legal, retry it. Once a
                // discard is actually accepted the hand/state changes and this body will
                // fail the stale/illegal check before another tile can be consumed.
                log.Info($"[AutoPlayLoop] repeated discard still actionable at state={currentState}; retrying dispatch");
            }

            EmitDecisionFinding("discard", snap, choice);
            DispatchPolicyChoice(snap, choice);
            log.Info($"[AutoPlayLoop] discard body done: {LastActionDescription} path={plugin.Dispatcher.LastDiscardPath}");
        });
    }

    /// <summary>On HookFailed clear the FSM context — otherwise the 3 s retry debounce keeps the bot stranded on a missed click.</summary>
    private void ClearRetryDebounceIfHookFailed(InputDispatcher.DispatchResult result)
    {
        if (result == InputDispatcher.DispatchResult.HookFailed)
            fsm.ClearContext();
    }

    private static Dictionary<string, object?> CallTraceData(StateSnapshot snap) => new()
    {
        ["state"] = snap.AddonStateCode,
        ["hand_count"] = snap.Hand.Count,
        ["hand"] = string.Join(",", snap.Hand),
        ["wall"] = snap.WallRemaining,
        ["legal"] = snap.Legal.Flags.ToString(),
        ["discardable"] = string.Join(",", snap.Legal.DiscardableTiles),
        ["pon_candidates"] = string.Join(" | ", snap.Legal.PonCandidates.Select(FormatCandidate)),
        ["chi_candidates"] = string.Join(" | ", snap.Legal.ChiCandidates.Select(FormatCandidate)),
        ["kan_candidates"] = string.Join(" | ", snap.Legal.KanCandidates.Select(FormatCandidate)),
        ["meld_count"] = snap.OurMelds.Count,
        ["turn"] = snap.TurnIndex,
    };

    private static string FormatCandidate(MeldCandidate candidate) =>
        $"kind={candidate.Kind};claimed={candidate.ClaimedTile};hand={string.Join(",", candidate.HandTiles)};from={candidate.FromSeat}";

    private static Dictionary<string, object?> ChoiceTraceData(StateSnapshot snap, ActionChoice choice, string? exitReason = null)
    {
        var data = CallTraceData(snap);
        data["choice_kind"] = choice.Kind.ToString();
        data["choice_tile"] = choice.DiscardTile?.ToString();
        data["choice_reasoning"] = choice.Reasoning;
        data["exit_reason"] = exitReason;
        return data;
    }

    private void ScheduleCallDecision(DispatchContext context)
    {
        plugin.ExecutionTrace.Record("call.schedule.enter", new Dictionary<string, object?>
        {
            ["fsm_dispatch_in_flight"] = fsm.IsDispatchInFlight,
            ["fsm_riichi_confirm_pending"] = fsm.IsRiichiConfirmPending,
            ["fsm_riichi_confirm_tile"] = fsm.RiichiConfirmTile?.ToString(),
            ["context_state"] = context.State,
            ["context_hand"] = context.Hand,
        });
        var initial = plugin.AddonReader.TryBuildSnapshot();
        if (initial is null)
        {
            plugin.ExecutionTrace.Record("call.schedule.exit", new Dictionary<string, object?> { ["reason"] = "snapshot-null" });
            return;
        }
        plugin.ExecutionTrace.Record("call.snapshot.initial", CallTraceData(initial));
        ActionChoice choice;
        try
        {
            choice = plugin.Policy.Choose(initial);
        }
        catch (Exception ex)
        {
            plugin.ExecutionTrace.Record("call.policy.exception", CallTraceData(initial), ex);
            log.Error(ex, "[AutoPlayLoop] call policy exception");
            fsm.ClearContext();
            return;
        }
        plugin.ExecutionTrace.Record("call.policy.choice", ChoiceTraceData(initial, choice));
        EmitDiagnosticDecision("call", initial, choice);

        // Never touch the call UI before the selected AI has returned an actual
        // decision for this prompt. Pending, unavailable and built-in fallback
        // sentinels are not instructions and must not be converted into Pass.
        if (Mahjong.Plugin.Dalamud.ExternalAi.SelectablePolicy.IsPendingChoice(choice))
        {
            LastActionDescription = $"waiting for AI call instruction: {choice.Reasoning}";
            log.Info("[AutoPlayLoop] {Description}", LastActionDescription);
            plugin.ExecutionTrace.Record("call.schedule.exit", ChoiceTraceData(initial, choice, "pending-ai"));
            fsm.ClearContext();
            return;
        }

        if (!HasAuthoritativeSelectedAiDecision())
        {
            string source = plugin.Policy is SelectablePolicy selectable
                ? selectable.LastDecisionSource
                : "不明";
            LastActionDescription = $"waiting for selected AI call instruction (source={source})";
            log.Info("[AutoPlayLoop] {Description}", LastActionDescription);
            plugin.ExecutionTrace.Record("call.schedule.exit", ChoiceTraceData(initial, choice, "wrong-ai-source"));
            fsm.ClearContext();
            return;
        }

        var preSwitchChoice = choice;
        choice = ApplyAutomaticCallSwitches(initial, choice);
        plugin.ExecutionTrace.Record("call.automatic-switches", new Dictionary<string, object?>
        {
            ["before_kind"] = preSwitchChoice.Kind.ToString(),
            ["after_kind"] = choice.Kind.ToString(),
            ["before_reason"] = preSwitchChoice.Reasoning,
            ["after_reason"] = choice.Reasoning,
        });
        if (!IsAutomaticPromptActionAllowed(choice))
        {
            LastActionDescription = $"automatic {choice.Kind} disabled — waiting for manual input";
            log.Info("[AutoPlayLoop] {Description}", LastActionDescription);
            plugin.ExecutionTrace.Record("call.schedule.exit", ChoiceTraceData(initial, choice, "automatic-action-disabled"));
            fsm.ClearContext();
            return;
        }

        string fingerprint = ActionFingerprint(initial, choice);
        // Riichi must not fire on the same frame Mortal answers. That looked
        // unnatural and could race the self-declare list animation. Use the normal
        // configurable human delay (default 3-4 s); the follow-up discard still has
        // its own 700 ms confirmation delay.
        TimeSpan delay = ResolveHumanDelay(choice, initial, context);
        plugin.ExecutionTrace.Record("call.schedule.queued", new Dictionary<string, object?>
        {
            ["fingerprint"] = fingerprint,
            ["delay_ms"] = delay.TotalMilliseconds,
            ["kind"] = choice.Kind.ToString(),
            ["tile"] = choice.DiscardTile?.ToString(),
        });
        ScheduleAction("call", context, delay, () =>
        {
            var snap = plugin.AddonReader.TryBuildSnapshot();
        plugin.ExecutionTrace.Record("autoplay.snapshot", snap is null ? new Dictionary<string, object?> { ["available"] = false } : new Dictionary<string, object?>
        {
            ["available"] = true, ["state"] = snap.AddonStateCode, ["hand"] = snap.Hand.Count, ["wall"] = snap.WallRemaining,
            ["legal"] = snap.Legal.Flags.ToString(), ["discardable"] = string.Join(",", snap.Legal.DiscardableTiles),
            ["pon_count"] = snap.Legal.PonCandidates.Count, ["chi_count"] = snap.Legal.ChiCandidates.Count, ["kan_count"] = snap.Legal.KanCandidates.Count,
            ["our_melds"] = snap.OurMelds.Count, ["turn"] = snap.TurnIndex,
        });
            int currentState = ReadStateCode();
            bool choiceStillLegal = snap is not null && IsChoiceStillLegal(snap, choice);
            if (choice.Kind == ActionKind.Riichi)
                choiceStillLegal = snap is not null
                    && snap.Hand.Contains(choice.DiscardTile!.Value)
                    && (snap.Legal.Can(ActionFlags.Riichi) || currentState == 6);
            plugin.ExecutionTrace.Record("call.execute.precheck", new Dictionary<string, object?>
            {
                ["snapshot_available"] = snap is not null,
                ["current_state"] = currentState,
                ["choice_still_legal"] = choiceStillLegal,
                ["kind"] = choice.Kind.ToString(),
                ["tile"] = choice.DiscardTile?.ToString(),
                ["legal"] = snap?.Legal.Flags.ToString(),
                ["hand"] = snap?.Hand.Count,
            });
            if (snap is null || !choiceStillLegal)
            {
                LastActionDescription = $"call aborted: stale or illegal decision (state={currentState})";
                plugin.ExecutionTrace.Record("call.execute.abort", new Dictionary<string, object?> { ["reason"] = "stale-or-illegal", ["state"] = currentState });
                log.Info($"[AutoPlayLoop] {LastActionDescription}");
                fsm.ClearContext();
                return;
            }
            // Call/self-declare prompts have a hard deadline and may remain visible even
            // after the addon reports an Ok dispatch. Never suppress a retry solely because
            // the prompt fingerprint is unchanged; doing so strands Chi/Pass/Riichi screens.
            // Keep duplicate suppression for ordinary discards only.
            if (IsDuplicateAction(fingerprint))
                log.Info("[AutoPlayLoop] repeated call prompt detected; retrying dispatch");

            if (snap.Legal.Can(ActionFlags.Riichi))
            {
                log.Info(
                    $"[RiichiPrompt] legal={snap.Legal.Flags} " +
                    $"mortalAction={choice.Kind} tile={choice.DiscardTile?.ToString() ?? "-"} " +
                    $"dispatch={(choice.Kind == ActionKind.Riichi ? "AutoRiichi" : "SkipRiichi")}");
            }

            EmitDecisionFinding("call", snap, choice);
            plugin.ExecutionTrace.Record("call.dispatch.begin", ChoiceTraceData(snap, choice));
            try
            {
                DispatchCallChoice(snap, choice);
                plugin.ExecutionTrace.Record("call.dispatch.end", new Dictionary<string, object?>
                {
                    ["description"] = LastActionDescription,
                    ["path"] = choice.Kind is ActionKind.Discard or ActionKind.Riichi
                        ? plugin.Dispatcher.LastDiscardPath
                        : ResolvePromptDispatchPath(
                            choice.Kind == ActionKind.Pass
                                ? ComputePassIndex(snap.Legal)
                                : null),
                    ["kind"] = choice.Kind.ToString(),
                });
            }
            catch (Exception ex)
            {
                plugin.ExecutionTrace.Record("call.dispatch.exception", ChoiceTraceData(snap, choice), ex);
                log.Error(ex, "[AutoPlayLoop] call dispatch exception");
                fsm.ClearContext();
                return;
            }
            log.Info($"[AutoPlayLoop] call dispatch sent; awaiting prompt closure: {LastActionDescription}");
        });
    }

    /// <summary>
    /// Completes the state-6 self-declare list protocol exactly once.
    /// <see cref="InputDispatcher.DispatchCallOption(int)"/> performs the native
    /// list row selection. EMJ emits the actual Riichi transition only after the
    /// addon-level opcode-11 commit arrives on a later UI tick. The current surface
    /// and latched tile are revalidated before committing, so a stale queued action
    /// cannot operate on a newer prompt.
    /// </summary>
    private void ScheduleRiichiListCommit(int option, Tile tile)
    {
        _ = framework.RunOnTick(() =>
        {
            try
            {
                if (disposed || !IsAutomationLoopEnabled())
                    return;

                if (!fsm.IsRiichiConfirmPending || fsm.RiichiConfirmTile != tile)
                    return;

                // Some addon builds complete SelectItem by themselves. In that case
                // the candidate list is already authoritative and a second callback
                // would be a duplicate declaration.
                if (plugin.Dispatcher.IsRiichiDiscardSelectionSurface())
                {
                    LastActionDescription = $"auto-riichi selection committed by list event (tile={tile})";
                    log.Info($"[AutoPlayLoop] {LastActionDescription}");
                    return;
                }

                var snap = plugin.AddonReader.TryBuildSnapshot();
                int state = ReadStateCode();
                bool samePrompt = snap is not null
                    && ShouldCommitRiichiListSelection(
                        state,
                        snap.Hand.Count,
                        snap.Hand.Contains(tile),
                        snap.Legal.Flags,
                        candidateSurfaceReady: false);

                plugin.ExecutionTrace.Record("riichi.list_commit.precheck", new Dictionary<string, object?>
                {
                    ["option"] = option,
                    ["tile"] = tile.ToString(),
                    ["state"] = state,
                    ["snapshot_available"] = snap is not null,
                    ["hand"] = snap?.Hand.Count,
                    ["legal"] = snap?.Legal.Flags.ToString(),
                    ["same_prompt"] = samePrompt,
                });

                if (!samePrompt)
                {
                    LastActionDescription =
                        $"auto-riichi commit cancelled: surface changed (tile={tile}, state={state}, hand={snap?.Hand.Count ?? -1}, legal={snap?.Legal.Flags.ToString() ?? "None"})";
                    log.Info($"[AutoPlayLoop] {LastActionDescription}");
                    fsm.ClearRiichiConfirm();
                    return;
                }

                var result = plugin.Dispatcher.CommitSelectedCallOption(option);
                MarkOperationDispatch(result, selectionSent: false, commitSent: true);
                LastActionDescription = $"auto-riichi-commit[opt={option}] (tile={tile}) → {result}";
                log.Info($"[AutoPlayLoop] {LastActionDescription}");
                plugin.GameLogger.RecordAction(
                    ActionKind.Riichi, tile, option, result.ToString(),
                    "state-6 list selection commit");
                EmitDispatchFinding("riichi-list-commit", result, option: option, tile: tile, snap: snap);
                ClearRetryDebounceIfHookFailed(result);
            }
            catch (Exception ex)
            {
                LastActionDescription = $"auto-riichi commit exception: {ex.Message}";
                plugin.ExecutionTrace.Record(
                    "riichi.list_commit.exception",
                    new Dictionary<string, object?>
                    {
                        ["option"] = option,
                        ["tile"] = tile.ToString(),
                    },
                    ex);
                log.Error(ex, "[AutoPlayLoop] Riichi list commit failed");
                fsm.ClearRiichiConfirm();
            }
        }, TimeSpan.Zero);
    }

    internal static bool ShouldCommitRiichiListSelection(
        int state,
        int handCount,
        bool chosenTilePresent,
        ActionFlags legal,
        bool candidateSurfaceReady) =>
        !candidateSurfaceReady
        && state == 6
        && handCount == 14
        && chosenTilePresent
        && (legal & ActionFlags.Riichi) != 0;

    private void ScheduleRiichiTsumogiri(DispatchContext context)
    {
        ScheduleAction("riichi-tsumogiri", context, RiichiTsumogiriDelayMs, () =>
        {
            var snap = plugin.AddonReader.TryBuildSnapshot();
        plugin.ExecutionTrace.Record("autoplay.snapshot", snap is null ? new Dictionary<string, object?> { ["available"] = false } : new Dictionary<string, object?>
        {
            ["available"] = true, ["state"] = snap.AddonStateCode, ["hand"] = snap.Hand.Count, ["wall"] = snap.WallRemaining,
            ["legal"] = snap.Legal.Flags.ToString(), ["discardable"] = string.Join(",", snap.Legal.DiscardableTiles),
            ["pon_count"] = snap.Legal.PonCandidates.Count, ["chi_count"] = snap.Legal.ChiCandidates.Count, ["kan_count"] = snap.Legal.KanCandidates.Count,
            ["our_melds"] = snap.OurMelds.Count, ["turn"] = snap.TurnIndex,
        });
            if (snap is null || snap.Hand.Count < 14)
            {
                LastActionDescription = $"riichi-tsumogiri aborted: hand={snap?.Hand.Count ?? -1}";
                return;
            }

            // Latch carries the policy-chosen tile; fall back to slot 13 only when no tile was latched (post-confirm yaku-preview popup).
            int slot;
            Tile tile;
            if (fsm.RiichiConfirmTile is { } target)
            {
                slot = plugin.AddonReader.FindAddonSlotOfTile(target);
                if (slot < 0)
                {
                    LastActionDescription = $"riichi-tsumogiri aborted: latched tile {target} not in hand";
                    log.Info($"[AutoPlayLoop] {LastActionDescription}");
                    return;
                }
                tile = target;
            }
            else
            {
                slot = 13;
                tile = snap.Hand[13];
            }

            if (!BeginOperation(
                    "riichi-tsumogiri",
                    ActionOperationKind.Discard,
                    snap,
                    tile: tile,
                    selectionOpcode: 15,
                    commitOpcode: 7,
                    commitArgument: slot))
            {
                LastActionDescription = "riichi-tsumogiri blocked by active operation transaction";
                return;
            }
            var result = plugin.Dispatcher.DispatchDiscard(slot);
            MarkDiscardOperationFromPath(result);
            LastActionDescription = $"auto-riichi-tsumogiri {tile} slot={slot} → {result}";
            log.Info($"[AutoPlayLoop] riichi-tsumogiri dispatch: {LastActionDescription}");
            plugin.GameLogger.RecordAction(ActionKind.Discard, tile, slot, result.ToString(), "riichi-tsumogiri");
            EmitDispatchFinding("riichi-tsumogiri", result, tile: tile, slot: slot, snap: snap);
            ClearRetryDebounceIfHookFailed(result);

            // The latch represents this one two-stage declaration only. Keeping it
            // for the whole hand caused every later stale Riichi/Pass surface to be
            // accepted again. Clear it after the single selected discard attempt;
            // failures are logged and are not retried automatically.
            fsm.ClearRiichiConfirm();
        });
    }


    private static int PickRedPreservingVariantIndex(IReadOnlyList<CallVariantOption> variants, out string note)
    {
        int bestIndex = 0;
        int bestRedCount = int.MaxValue;
        for (int i = 0; i < variants.Count; i++)
        {
            int redCount = variants[i].Tiles.Count(tile => tile.IsRed);
            if (redCount < bestRedCount)
            {
                bestRedCount = redCount;
                bestIndex = i;
            }
        }
        note = $"red-preserving fallback pattern {bestIndex}";
        return bestIndex;
    }

    private void DispatchPolicyChoice(StateSnapshot snap, ActionChoice choice)
    {
        if (choice.Kind == ActionKind.AnKan && choice.DiscardTile is { } kanTile)
        {
            DispatchAnkan(snap, choice, kanTile);
            return;
        }

        if (choice.Kind == ActionKind.Pass && IsHandOutOfSyncReason(choice.Reasoning)
            && snap.Legal.Can(ActionFlags.Discard))
        {
            DispatchOutOfSyncTsumogiri(snap, choice);
            return;
        }

        if (choice.Kind != ActionKind.Discard && choice.Kind != ActionKind.Riichi)
        {
            LastActionDescription = $"policy returned {choice.Kind} — not dispatching";
            return;
        }
        if (choice.DiscardTile is null)
        {
            LastActionDescription = $"policy {choice.Kind} missing tile";
            return;
        }

        DispatchDiscardOrRiichi(snap, choice);
    }

    /// <summary>
    /// Matches <c>EfficiencyPolicy.TsumogiriFallback</c>'s pause reason. When the meld tracker
    /// has fallen behind (typically post-ShouMinKan or a missed pon/chi inference race), the
    /// policy refuses to score a hand whose closed+meld arithmetic ≠ 14. Without a recovery
    /// path the autoplay loop debounces forever on the same Pass and the bot softlocks.
    /// </summary>
    private static bool IsHandOutOfSyncReason(string? reasoning) =>
        reasoning is not null
        && reasoning.StartsWith("hand state out of sync", StringComparison.Ordinal);

    /// <summary>
    /// Blind tsumogiri at slot 13 — the addon parks the just-drawn tile there even under the
    /// post-call sparse layout (HandArrayDecoder.cs:7). Safer than guessing a real discard
    /// off an incomplete view, and keeps the hand moving instead of softlocking.
    /// </summary>
    private void DispatchOutOfSyncTsumogiri(StateSnapshot snap, ActionChoice passChoice)
    {
        if (snap.Hand.Count == 0)
        {
            LastActionDescription = "oos-tsumogiri aborted: empty hand";
            log.Warning($"[AutoPlayLoop] {LastActionDescription}");
            return;
        }

        // Hand[^1] mirrors the slot-13-preferred decode order in HandArrayDecoder.ReadHand
        // (scans 0..len-1, so the highest occupied slot ends up last). FindAddonSlotOfTile
        // resolves it back to slot 13 when the tile sits there, otherwise to its actual slot.
        Tile drawn = snap.Hand[^1];
        int slot = plugin.AddonReader.FindAddonSlotOfTile(drawn);
        if (slot < 0)
        {
            LastActionDescription = $"oos-tsumogiri aborted: drawn tile {drawn} not in addon hand";
            log.Warning($"[AutoPlayLoop] {LastActionDescription}");
            return;
        }

        if (!BeginOperation(
                "oos-tsumogiri",
                ActionOperationKind.Discard,
                snap,
                tile: drawn,
                selectionOpcode: 15,
                commitOpcode: 7,
                commitArgument: slot))
        {
            LastActionDescription = "oos-tsumogiri blocked by active operation transaction";
            return;
        }
        var result = plugin.Dispatcher.DispatchDiscard(slot);
        MarkDiscardOperationFromPath(result);
        LastActionDescription = $"oos-tsumogiri recovery {drawn} slot={slot} → {result}";
        log.Warning(
            $"[AutoPlayLoop] {LastActionDescription} " +
            $"(policy reason: {passChoice.Reasoning})");
        plugin.GameLogger.RecordAction(
            ActionKind.Discard, drawn, slot, result.ToString(),
            $"oos-tsumogiri: {passChoice.Reasoning}");
        EmitDispatchFinding("oos-tsumogiri", result, tile: drawn, slot: slot, snap: snap);
        ClearRetryDebounceIfHookFailed(result);
    }

    private void DispatchAnkan(StateSnapshot snap, ActionChoice choice, Tile kanTile)
    {
        // Route AnKan through opcode 11 (call-prompt button-row) — the speculative opcode-12 path was a no-op in the addon.
        int acceptIndex = ComputeAcceptIndex(ActionKind.AnKan, snap.Legal, null);
        if (!BeginOperation(
                "ankan",
                ActionOperationKind.SelfCall,
                snap,
                choice,
                kanTile,
                selectionOpcode: 11,
                selectionArgument: acceptIndex,
                commitOpcode: 11,
                commitArgument: acceptIndex))
        {
            LastActionDescription = "auto-ankan blocked by active operation transaction";
            return;
        }
        var result = plugin.Dispatcher.DispatchCallOption(acceptIndex);
        MarkOperationDispatch(result, selectionSent: true, commitSent: true);
        LastActionDescription = $"auto-ankan {kanTile} opt={acceptIndex} → {result}";
        plugin.GameLogger.RecordAction(ActionKind.AnKan, kanTile, acceptIndex, result.ToString(), choice.Reasoning);
        EmitDispatchFinding("ankan", result, option: acceptIndex, tile: kanTile, snap: snap);
        ClearRetryDebounceIfHookFailed(result);

        // Self-declared kans produce no opp-discard signal; MeldTracker.ObserveSnapshot cannot infer them, so record here to preserve the 14-tile invariant.
        if (result == InputDispatcher.DispatchResult.Ok)
            plugin.MeldTracker.Record(Meld.AnKan(kanTile));
    }

    private void DispatchDiscardOrRiichi(StateSnapshot snap, ActionChoice choice)
    {
        var tile = choice.DiscardTile!.Value;
        int slot = plugin.AddonReader.FindAddonSlotOfTile(tile);
        if (slot < 0)
        {
            LastActionDescription = $"tile {tile} not in hand";
            return;
        }

        if (choice.Kind == ActionKind.Riichi)
        {
            // Never degrade a Mortal reach decision into an ordinary discard.
            // EMJ can briefly report Discard/Chi/Pass before the Riichi option
            // becomes visible. In that transient state, release the context and
            // retry on the next actionable snapshot instead of throwing away the
            // reach declaration.
            // EMJ sometimes omits the Riichi bit while the state-6 SelfDeclareList
            // is already visible (the UI visibly shows Riichi / Pass). In that case,
            // waiting for Legal.Riichi deadlocks forever. State code 6 is the reliable
            // signal for this popup and option 0 is always Riichi there.
            int stateCode = ReadStateCode();
            bool riichiPopupVisible = stateCode == 6;
            if (!snap.Legal.Can(ActionFlags.Riichi) && !riichiPopupVisible)
            {
                LastActionDescription = $"auto-riichi waiting for popup (tile={tile}, state={stateCode}, legal={snap.Legal.Flags})";
                log.Info($"[AutoPlayLoop] {LastActionDescription}");
                fsm.ClearContext();
                return;
            }

            int riichiIdx = riichiPopupVisible
                ? 0
                : ComputeAcceptIndex(ActionKind.Riichi, snap.Legal, null);
            if (!BeginOperation(
                    "riichi-declaration",
                    ActionOperationKind.RiichiDeclaration,
                    snap,
                    choice,
                    tile,
                    commitOpcode: 11,
                    commitArgument: riichiIdx))
            {
                LastActionDescription = "auto-riichi blocked by active operation transaction";
                return;
            }
            var rResult = plugin.Dispatcher.DispatchCallOption(riichiIdx);
            MarkOperationDispatch(rResult, selectionSent: true, commitSent: false);
            LastActionDescription = $"auto-riichi[opt={riichiIdx}] (tile={tile}) → {rResult}";
            plugin.GameLogger.RecordAction(ActionKind.Riichi, tile, riichiIdx, rResult.ToString(), choice.Reasoning);
            EmitDispatchFinding("riichi", rResult, option: riichiIdx, tile: tile, snap: snap);
            if (rResult == InputDispatcher.DispatchResult.Ok)
            {
                fsm.LatchRiichiConfirm(tile);
                // State 6 is an AtkComponentList. SelectItem is only the native
                // row-selection stage on the Japanese client; the addon-level
                // opcode-11 commit must run on the following framework tick.
                // Scheduling exactly one validated commit restores the captured
                // SelectItem -> [11, option] protocol without retrying Riichi.
                ScheduleRiichiListCommit(riichiIdx, tile);

                // The next meaningful action is the discard-candidate surface,
                // which can retain the same state/hand context. Release only the
                // context debounce; the Riichi latch blocks any second declaration.
                fsm.ClearContext();
            }
            ClearRetryDebounceIfHookFailed(rResult);
            return;
        }

        if (!BeginOperation(
                "discard",
                ActionOperationKind.Discard,
                snap,
                choice,
                tile,
                selectionOpcode: 15,
                commitOpcode: 7,
                commitArgument: slot))
        {
            LastActionDescription = "auto-discard blocked by active operation transaction";
            return;
        }
        var result = plugin.Dispatcher.DispatchDiscard(slot);
        MarkDiscardOperationFromPath(result);
        LastActionDescription = $"auto-discard {tile} slot={slot} → {result}";
        plugin.GameLogger.RecordAction(ActionKind.Discard, tile, slot, result.ToString(), choice.Reasoning);
        EmitDispatchFinding("discard", result, tile: tile, slot: slot, snap: snap);
        ClearRetryDebounceIfHookFailed(result);
    }

    private void DispatchCallChoice(StateSnapshot snap, ActionChoice choice)
    {
        var legal = snap.Legal;

        // State-6 popup is dual-use: it offers Riichi/Tsumo/AnKan and lists discardable tiles — route Discard/Riichi through the list-widget path, not Pass.
        if (choice.Kind is ActionKind.Discard or ActionKind.Riichi
            && choice.DiscardTile.HasValue)
        {
            DispatchPolicyChoice(snap, choice);
            log.Info($"[AutoPlayLoop] discard-from-call-popup dispatch: {LastActionDescription}");
            return;
        }

        bool acceptRiichiPopup = ResolveRiichiPopupAcceptance(snap, choice, out var riichiProbeTile, out var riichiReason);

        bool shouldAccept = acceptRiichiPopup || choice.Kind is
            ActionKind.Ron or ActionKind.Tsumo or
            ActionKind.Pon or ActionKind.Chi or
            ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan;

        if (shouldAccept)
            DispatchAccept(snap, choice, legal, acceptRiichiPopup, riichiProbeTile, riichiReason!);
        else
            DispatchPass(snap, choice, legal, riichiReason);

        log.Info($"[AutoPlayLoop] call-prompt dispatch: {LastActionDescription}");
    }

    /// <summary>Preserves an explicit Pass. Riichi is accepted only when the selected policy returned Riichi directly.</summary>
    private bool ResolveRiichiPopupAcceptance(StateSnapshot snap, ActionChoice choice, out Tile? probeTile, out string? probeReason)
    {
        probeTile = null;
        probeReason = choice.Reasoning;

        // Policy.Choose already returns Riichi when the selected AI wants to
        // declare. An explicit Pass must remain Pass. Re-running another policy
        // here previously converted Akochan's Pass into a second Riichi click,
        // especially while EMJ retained a stale Riichi/Pass signature.
        return false;
    }

    private void DispatchAccept(StateSnapshot snap, ActionChoice choice, LegalActions legal, bool acceptRiichiPopup, Tile? riichiProbeTile, string riichiReason)
    {
        // Every accept flows through opcode 11 / SelectItem (DispatchCallOption auto-routes by popup shape). The dedicated Tsumo opcode-9 path no-opped at state-6 SelfDeclareList because that popup is a list widget — the corpus capture of opcode 9 was the addon's internal callback fired *after* SelectItem ran, not a click-equivalent payload.
        var loggedKind = acceptRiichiPopup ? ActionKind.Riichi : choice.Kind;
        int acceptIndex = acceptRiichiPopup
            ? ComputeAcceptIndex(ActionKind.Riichi, legal, choice.Call)
            : ComputeAcceptIndex(choice.Kind, legal, choice.Call);
        if (choice.Call is not null && choice.Kind is
            ActionKind.Pon or ActionKind.Chi or ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan)
            pendingVariantChoice = choice;
        else
            pendingVariantChoice = null;

        string label = acceptRiichiPopup ? "riichi-confirm" : choice.Kind.ToString().ToLowerInvariant();
        bool needsVariantCommit = choice.Kind == ActionKind.Chi && legal.ChiCandidates.Count > 1;
        ActionOperationKind operationKind = acceptRiichiPopup
            ? ActionOperationKind.RiichiDeclaration
            : IsCommittedOpenCall(choice)
                ? ActionOperationKind.OpenCall
                : choice.Kind is ActionKind.AnKan or ActionKind.ShouMinKan
                    ? ActionOperationKind.SelfCall
                    : ActionOperationKind.PromptAction;
        int? commitOpcode = needsVariantCommit ? 12 : 11;
        int? commitArgument = needsVariantCommit ? null : acceptIndex;
        if (!BeginOperation(
                label,
                operationKind,
                snap,
                choice,
                choice.DiscardTile ?? choice.Call?.ClaimedTile,
                selectionOpcode: 11,
                selectionArgument: acceptIndex,
                commitOpcode: commitOpcode,
                commitArgument: commitArgument))
        {
            LastActionDescription = $"auto-{label} blocked by active operation transaction";
            return;
        }

        var result2 = plugin.Dispatcher.DispatchCallOption(acceptIndex);
        MarkOperationDispatch(result2, selectionSent: true, commitSent: !needsVariantCommit);
        LastActionDescription = $"auto-{label}[opt={acceptIndex}] → {result2}";
        plugin.GameLogger.RecordAction(
            loggedKind, null, acceptIndex, result2.ToString(),
            acceptRiichiPopup ? riichiReason : choice.Reasoning);
        EmitDispatchFinding(
            label, result2, option: acceptIndex, snap: snap,
            committedChoice: IsCommittedOpenCall(choice) ? choice : null);
        if (result2 == InputDispatcher.DispatchResult.Ok
            && IsCommittedOpenCall(choice)
            && plugin.Policy is SelectablePolicy selectable)
        {
            // Preserve the exact candidate before EMJ asynchronously shrinks the
            // hand. Structural confirmation still happens in CheckPendingDispatchOutcome;
            // this only closes the process-restart gap between those two events.
            selectable.NotifyDispatchedOpenCall(choice, snap);
        }
        ClearRetryDebounceIfHookFailed(result2);

        // All prompt actions are single-dispatch. A successful Riichi selection
        // proceeds through the structural discard-candidate transition handled at
        // the top of the loop; no timed re-clicks are issued.
        if (result2 == InputDispatcher.DispatchResult.Ok && acceptRiichiPopup)
        {
            fsm.LatchRiichiConfirm(riichiProbeTile);
            fsm.ClearContext();
        }

        // ShouMinKan: addon shrinks the closed hand by 1 and the existing pon ought to grow to a kan,
        // but ObserveSnapshot can't infer that from a delta=1. Upgrade the meld in-place so meld-tile
        // arithmetic stays at 14 and the policy doesn't fall into out-of-sync Pass.
        if (result2 == InputDispatcher.DispatchResult.Ok
            && choice.Kind == ActionKind.AnKan
            && choice.Call is { } ankanCand)
        {
            plugin.MeldTracker.Record(Meld.AnKan(ankanCand.ClaimedTile));
        }

        if (result2 == InputDispatcher.DispatchResult.Ok
            && choice.Kind == ActionKind.ShouMinKan
            && choice.Call is { } shouCand)
        {
            plugin.MeldTracker.UpgradeToShouMinKan(shouCand.ClaimedTile);
        }
    }

    private void DispatchPass(StateSnapshot snap, ActionChoice choice, LegalActions legal, string? reasonOverride = null)
    {
        // Pass index = count of accept buttons (multi-chi adds one slot per chi candidate).
        int passIndex = ComputePassIndex(legal);
        if (!BeginOperation(
                "pass",
                ActionOperationKind.PromptAction,
                snap,
                choice,
                selectionOpcode: 11,
                selectionArgument: passIndex,
                commitOpcode: 11,
                commitArgument: passIndex))
        {
            LastActionDescription = "auto-pass blocked by active operation transaction";
            return;
        }
        var result = plugin.Dispatcher.DispatchCallOption(passIndex);
        MarkOperationDispatch(result, selectionSent: true, commitSent: true);
        LastActionDescription = $"auto-pass[opt={passIndex}] → {result}";
        string reasoning = string.IsNullOrEmpty(reasonOverride) ? choice.Reasoning : reasonOverride;
        plugin.GameLogger.RecordAction(ActionKind.Pass, null, passIndex, result.ToString(), reasoning);
        EmitDispatchFinding("pass", result, option: passIndex, snap: snap, dispatchPath: ResolvePromptDispatchPath(passIndex));
        // Pass is a single callback. Re-sending while the same row remains
        // visible can act on a later prompt after EMJ advances asynchronously.
        // Completion is confirmed only by the normal addon state transition.
    }

    /// <summary>Call-row button order Pon, Chi, AnKan, MinKan, ShouMinKan, Ron, Riichi, Tsumo, Pass; Chi is one slot regardless of ChiCandidates.Count (variant picked in state-25 sub-popup).</summary>
    internal static int ComputeAcceptIndex(ActionKind kind, LegalActions legal, MeldCandidate? chosenCall)
    {
        int idx = 0;

        if (kind == ActionKind.Pon)
            return idx;
        if (legal.Can(ActionFlags.Pon))
            idx++;

        if (kind == ActionKind.Chi)
            return idx;
        if (legal.Can(ActionFlags.Chi))
            idx++;

        if (kind == ActionKind.AnKan)
            return idx;
        if (legal.Can(ActionFlags.AnKan))
            idx++;

        if (kind == ActionKind.MinKan)
            return idx;
        if (legal.Can(ActionFlags.MinKan))
            idx++;

        if (kind == ActionKind.ShouMinKan)
            return idx;
        if (legal.Can(ActionFlags.ShouMinKan))
            idx++;

        if (kind == ActionKind.Ron)
            return idx;
        if (legal.Can(ActionFlags.Ron))
            idx++;

        if (kind == ActionKind.Riichi)
            return idx;
        if (legal.Can(ActionFlags.Riichi))
            idx++;

        if (kind == ActionKind.Tsumo)
            return idx;

        return 0;
    }

    /// <summary>Index of the Pass button on a call-prompt row: one slot per offered accept action (see <see cref="ComputeAcceptIndex"/>), Pass closes the row.</summary>
    internal static int ComputePassIndex(LegalActions legal)
    {
        int idx = 0;
        if (legal.Can(ActionFlags.Pon))
            idx++;
        if (legal.Can(ActionFlags.Chi))
            idx++;
        if (legal.Can(ActionFlags.AnKan))
            idx++;
        if (legal.Can(ActionFlags.MinKan))
            idx++;
        if (legal.Can(ActionFlags.ShouMinKan))
            idx++;
        if (legal.Can(ActionFlags.Ron))
            idx++;
        if (legal.Can(ActionFlags.Riichi))
            idx++;
        if (legal.Can(ActionFlags.Tsumo))
            idx++;
        return idx;
    }

    private unsafe int ReadStateCode()
    {
        if (!addon.TryGet(out var unit, out _))
            return -1;
        if (!unit->IsVisible || unit->AtkValues == null || unit->AtkValuesCount == 0)
            return -1;
        var v = unit->AtkValues[0];
        return v.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int ? v.Int : -1;
    }
}
