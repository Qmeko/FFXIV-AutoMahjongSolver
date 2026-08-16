# Mortal AI integration — v0.3.0.0

Implemented:

- Standalone `DomanMahjongSolverDebug` identity and `/mjdebug` command.
- Default `Mortal AI (installed runtime)` decision provider.
- One-click installation of the public Akagi-MjaiBot-Mortal four-player runtime.
- Managed Python 3.12 and CPU PyTorch environment installed with `uv`.
- Real model-load and array-batched mjai JSONL smoke test during setup.
- Persistent Mortal subprocess with startup/response timeouts and stderr diagnostics.
- One JSON array of mjai events per request, matching the Mortal adapter contract.
- Initial-hand bootstrap with visible opening draws/discards and current action opportunity.
- Incremental draw, discard, riichi, dora and meld event tracking.
- Mapping for discard, pass, riichi, tsumo, ron, pon, chi, ankan, daiminkan and kakan.
- Exact chi-variant latching for the state-25 selection popup.
- Legal-action validation before dispatch.
- Built-in heuristic fallback on missing runtime, timeout, crash, malformed output or illegal action.
- Board-state decision cache shared by hints and auto-play.
- Local/online Mortal settings and runtime status in the plugin UI.
- Configuration schema v4 migration.
- Separate developer-plugin packaging and registration instructions.

Known limits:

- The public local Mortal release includes the publicly distributed model; it is not guaranteed to match stronger private/hosted weights.
- AI quality is bounded by what the current Doman Mahjong addon reader exposes. Unknown opponent meld/riichi, seat, honba, kyotaku or red-five details reduce decision quality.
- JP-client memory/layout behavior still requires live validation.

## v0.4.0.0 humanized autoplay and safety
- Per-action randomized delay ranges.
- Estimated 15-second turn budget with emergency shortening.
- Mortal/Built-in/Fallback source and inference diagnostics.
- Configurable Mortal CPU thread count and automatic restart.
- Legality recheck immediately before dispatch.
- Duplicate action fingerprint suppression.


## v0.5.0.0 diagnostic decision logging
- Added opt-in/default-on /xllog records for MahjongSnapshot, OpponentSnapshot, MortalInput and MortalDecision.
- Records are emitted only when a discard/call decision is requested.
- Added configuration migration 5 -> 6 and Developer settings toggle.


## v0.5.1.0 Mortal decision/session repair
- Treat an empty `DiscardableTiles` list as all closed-hand tiles selectable.
- Accept valid Mortal `dahai` responses such as `1m` instead of false illegal-action fallback.
- Do not kill the synchronized Mortal process for one rejected action.
- Permit closed-hand mid-kyoku session recovery after a Mortal process restart.
- Keep fallback scoped to the failed decision rather than the remainder of the hand.
