using Mahjong.Engine;
using Mahjong.Policy;

namespace Mahjong.Plugin.Dalamud.Actions;

internal enum ActionOperationKind
{
    Discard,
    PostCallDiscard,
    RiichiDeclaration,
    OpenCall,
    SelfCall,
    PromptAction,
}

internal enum ActionOperationPhase
{
    Created,
    SelectionSent,
    SelectionObserved,
    CommitSent,
    CommitObserved,
    StructurallyConfirmed,
    ClosedWithoutCommit,
    Cancelled,
}

internal readonly record struct ActionOperationSnapshot(
    int State,
    int HandCount,
    int MeldCount,
    int OwnDiscardCount,
    int TotalDiscardCount,
    int WallRemaining,
    ActionFlags Legal,
    bool OurRiichi,
    bool RiichiDiscardSurfaceReady);

internal sealed class ActionOperationTransaction
{
    public ActionOperationTransaction(
        string label,
        ActionOperationKind kind,
        ActionOperationSnapshot baseline,
        ActionChoice? choice,
        Tile? tile,
        int? expectedSelectionOpcode,
        int? expectedSelectionArgument,
        int? expectedCommitOpcode,
        int? expectedCommitArgument)
    {
        Label = label;
        Kind = kind;
        Baseline = baseline;
        Choice = choice;
        Tile = tile;
        ExpectedSelectionOpcode = expectedSelectionOpcode;
        ExpectedSelectionArgument = expectedSelectionArgument;
        ExpectedCommitOpcode = expectedCommitOpcode;
        ExpectedCommitArgument = expectedCommitArgument;
        StartedAtUtc = DateTime.UtcNow;
    }

    public string Label { get; }
    public ActionOperationKind Kind { get; }
    public ActionOperationSnapshot Baseline { get; }
    public ActionChoice? Choice { get; }
    public Tile? Tile { get; }
    public DateTime StartedAtUtc { get; }

    public int? ExpectedSelectionOpcode { get; private set; }
    public int? ExpectedSelectionArgument { get; private set; }
    public int? ExpectedCommitOpcode { get; private set; }
    public int? ExpectedCommitArgument { get; private set; }

    public bool SelectionSent { get; private set; }
    public bool SelectionObserved { get; private set; }
    public bool CommitSent { get; private set; }
    public bool CommitObserved { get; private set; }
    public bool StructurallyConfirmed { get; private set; }
    public bool ClosedWithoutCommit { get; private set; }
    public bool Cancelled { get; private set; }

    public bool IsTerminal => StructurallyConfirmed || ClosedWithoutCommit || Cancelled;

    public ActionOperationPhase Phase =>
        Cancelled ? ActionOperationPhase.Cancelled :
        ClosedWithoutCommit ? ActionOperationPhase.ClosedWithoutCommit :
        StructurallyConfirmed ? ActionOperationPhase.StructurallyConfirmed :
        CommitObserved ? ActionOperationPhase.CommitObserved :
        CommitSent ? ActionOperationPhase.CommitSent :
        SelectionObserved ? ActionOperationPhase.SelectionObserved :
        SelectionSent ? ActionOperationPhase.SelectionSent :
        ActionOperationPhase.Created;

    public void MarkSelectionSent() => SelectionSent = true;
    public void MarkCommitSent() => CommitSent = true;

    public void SetExpectedCommit(int opcode, int? argument)
    {
        ExpectedCommitOpcode = opcode;
        ExpectedCommitArgument = argument;
    }

    public bool ObserveAgentEvent(int opcode, int? argument)
    {
        bool changed = false;
        if (Matches(ExpectedSelectionOpcode, ExpectedSelectionArgument, opcode, argument))
        {
            SelectionObserved = true;
            changed = true;
        }
        if (Matches(ExpectedCommitOpcode, ExpectedCommitArgument, opcode, argument))
        {
            CommitObserved = true;
            changed = true;
            if (ExpectedSelectionOpcode == ExpectedCommitOpcode)
                SelectionObserved = true;
        }
        return changed;
    }

    public bool ObserveSnapshot(ActionOperationSnapshot current)
    {
        if (IsTerminal)
            return false;

        // A real hand transition invalidates every queued UI operation. This is
        // structural evidence, not a timer-based expiry.
        if (current.WallRemaining > Baseline.WallRemaining + 5)
        {
            Cancelled = true;
            return true;
        }

        bool confirmed = Kind switch
        {
            ActionOperationKind.Discard or ActionOperationKind.PostCallDiscard =>
                current.HandCount < Baseline.HandCount
                || current.OwnDiscardCount > Baseline.OwnDiscardCount,

            ActionOperationKind.RiichiDeclaration =>
                current.OurRiichi
                || current.RiichiDiscardSurfaceReady
                || current.HandCount < Baseline.HandCount
                || current.OwnDiscardCount > Baseline.OwnDiscardCount,

            ActionOperationKind.OpenCall or ActionOperationKind.SelfCall =>
                current.HandCount < Baseline.HandCount
                || current.MeldCount > Baseline.MeldCount,

            ActionOperationKind.PromptAction =>
                // Prompt callbacks (Pass/Ron/Tsumo) can briefly expose state
                // 19/22 while the old rows are still alive. Do not complete on
                // that transient code alone: require the AgentEmj commit event,
                // or independent public progression that proves the prompt ended.
                (CommitObserved
                    && (current.State != Baseline.State || current.Legal != Baseline.Legal))
                || current.TotalDiscardCount > Baseline.TotalDiscardCount
                || current.WallRemaining < Baseline.WallRemaining,

            _ => false,
        };

        if (confirmed)
        {
            StructurallyConfirmed = true;
            return true;
        }

        // An opponent-discard call was explicitly closed without our concealed
        // hand or melds changing. This is the only non-call completion path and
        // prevents a stale accepted-call transaction from leaking into the next
        // prompt. It relies on public game progression rather than elapsed time.
        if (Kind == ActionOperationKind.OpenCall
            && current.HandCount == Baseline.HandCount
            && current.MeldCount == Baseline.MeldCount
            && (current.TotalDiscardCount > Baseline.TotalDiscardCount
                || current.WallRemaining < Baseline.WallRemaining)
            && !HasExternalCallFlag(current.Legal))
        {
            ClosedWithoutCommit = true;
            return true;
        }

        return false;
    }

    public void Cancel() => Cancelled = true;

    private static bool Matches(int? expectedOpcode, int? expectedArgument, int opcode, int? argument) =>
        expectedOpcode.HasValue
        && expectedOpcode.Value == opcode
        && (!expectedArgument.HasValue || expectedArgument == argument);

    private static bool HasExternalCallFlag(ActionFlags legal) =>
        (legal & (ActionFlags.Pon | ActionFlags.Chi | ActionFlags.MinKan | ActionFlags.Pass)) != 0;
}
