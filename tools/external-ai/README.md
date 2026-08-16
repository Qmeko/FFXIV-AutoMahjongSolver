# Mortal / external mjai runtime

`BUILD_DEBUG_PLUGIN.bat` builds the Dalamud developer plugin and installs a four-player Mortal runtime under:

```text
%LOCALAPPDATA%\DomanMahjongSolverDebug\MortalRuntime
```

The installer downloads the public `release4p.zip` from `shinkuan/Akagi-MjaiBot-Mortal`, installs a managed Python 3.12 runtime with `uv`, installs PyTorch/Numpy/Requests, and performs a real model + JSONL round-trip smoke test.

The plugin protocol is one JSON array of mjai events per stdin line and exactly one JSON action per stdout line. stderr is reserved for diagnostics.

## Modes

- **Mortal AI (installed runtime)**: default. No manual executable path is required.
- **Built-in heuristic**: original C# policy.
- **External mjai process**: manual JSONL bot path for development.

## Public model limitation

The public release contains a small placeholder model. It proves the complete integration and can play automatically, but it is not the strongest available Mortal model. The Settings window also supports the adapter's optional online inference server and API key.

## FFXIV state limitation

Mortal can only use information present in `StateSnapshot`. Incorrect or unavailable seat, opponent meld, riichi, discard-order, honba or kyotaku data reduces its strength. Invalid or unsupported output is rejected and falls back to the built-in policy by default.
