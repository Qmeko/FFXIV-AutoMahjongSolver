# Doman Mahjong Solver Debug + Mortal AI

This package builds a standalone debug plugin and installs a local Mortal
runtime. It is isolated from the normal Doman Mahjong Solver.

## Isolation

- Plugin: `Doman Mahjong Solver Debug`
- Internal name / assembly: `DomanMahjongSolverDebug`
- Command: `/mjdebug`
- Separate configuration and logs
- Startup forces auto-play OFF, hints ON, developer mode ON and game logging ON
- Remote plugin telemetry is disabled

## One-click build

Run only:

```text
BUILD_DEBUG_PLUGIN.bat
```

The script builds the plugin, installs the public Akagi-MjaiBot-Mortal
four-player runtime, creates a managed Python 3.12 CPU environment and runs
a real model/mjai round-trip test.

Developer DLL:

```text
OUTPUT\DomanMahjongSolverDebug\DomanMahjongSolverDebug.dll
```

Mortal runtime:

```text
%LOCALAPPDATA%\DomanMahjongSolverDebug\MortalRuntime
```

## Main commands

- `/mjdebug`
- `/mjdebug debug`
- `/mjdebug variant dump`
- `/mjdebug capture <label>`
- `/mjdebug snap <label>`
- `/mjdebug autosnap on|off`
- `/mjdebug log on|off`

The normal `/mjauto` command is not registered.
