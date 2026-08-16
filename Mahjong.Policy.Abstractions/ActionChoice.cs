namespace Mahjong.Policy.Abstractions;

public enum ActionKind : byte
{
    Pass,
    Discard,
    Riichi,
    Tsumo,
    Ron,
    Pon,
    Chi,
    AnKan,
    MinKan,
    ShouMinKan,
}

/// <summary>
/// <see cref="Reasoning"/> is a human summary; <see cref="Steps"/> is the structured
/// per-evaluator rationale chain.
/// </summary>
public sealed record ActionChoice(
    ActionKind Kind,
    Tile? DiscardTile = null,
    MeldCandidate? Call = null,
    string Reasoning = "",
    IReadOnlyList<Reason>? Steps = null)
{
    /// <summary>
    /// Physical red-five flags for <see cref="MeldCandidate.HandTiles"/> when an
    /// external mjai engine returned an exact consumed array (0m/0p/0s). This
    /// keeps multi-pattern call selection exact without changing the 34-tile
    /// logical model used by the policy engine.
    /// </summary>
    public IReadOnlyList<bool> CallConsumedRed { get; init; } = [];

    /// <summary>
    /// Exact mandatory discard returned by an mjai engine in the same response
    /// array as a Chi or Pon. Akochan emits [call, dahai] atomically; retaining
    /// this tile prevents a second, invalid selector request on the committed
    /// open-call event.
    /// </summary>
    public Tile? PostCallDiscardTile { get; init; }

    public IReadOnlyList<Reason> ReasonSteps => Steps ?? [];

    public static ActionChoice Pass(string why = "", IReadOnlyList<Reason>? steps = null) =>
        new(ActionKind.Pass, Reasoning: why, Steps: steps);

    public static ActionChoice Discard(Tile t, string why = "", IReadOnlyList<Reason>? steps = null) =>
        new(ActionKind.Discard, DiscardTile: t, Reasoning: why, Steps: steps);

    public static ActionChoice DeclareRiichi(Tile discard, string why = "", IReadOnlyList<Reason>? steps = null) =>
        new(ActionKind.Riichi, DiscardTile: discard, Reasoning: why, Steps: steps);

    public static ActionChoice DeclareTsumo(string why = "") =>
        new(ActionKind.Tsumo, Reasoning: why);

    public static ActionChoice DeclareRon(string why = "") =>
        new(ActionKind.Ron, Reasoning: why);
}
