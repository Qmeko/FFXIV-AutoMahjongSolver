# Doman Mahjong Solver Debug + Mortal AI

[日本語](README.md)

A Dalamud plugin that reads Doman Mahjong and uses Mortal / Akochan for discard, calls, riichi, and win actions.

Original project: [XeldarAlz/FFXIV-DomanMahjongSolver](https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver)

## Install

1. Run `/xlsettings` and open the **Experimental** tab
2. Add this URL under **Custom Plugin Repositories** and enable it

```
https://raw.githubusercontent.com/Qmeko/DalamudPlugins/refs/heads/main/pluginmaster.json
```

3. Run `/xlplugins` and install **ドマ式麻雀ソルバー デバッグ版**
4. Enable the plugin

You do not need to build from source.

## First-run setup

- **Akochan** is included in the plugin zip
- **Mortal AI** is downloaded automatically on first launch (Python / PyTorch / the public model; several minutes, a few hundred MB)
- Progress is printed in chat. If it fails, retry from **判断AI** in Settings

Runtime location:

```text
%LOCALAPPDATA%\DomanMahjongSolverDebug\MortalRuntime
```

## Usage

1. Open the window with `/mjdebug`
2. The plugin starts in **Hints** mode for safety
3. Check the board readout and AI connection
4. Turn on Auto-play only when you want automatic actions

Switch between Mortal and Akochan in Settings.

## Commands

| Command | Description |
|---|---|
| `/mjdebug` | Toggle the main window |

## Notes

- Mortal uses a public local model. It is not as strong as private top-tier models
- Hidden information such as opponent melds or riichi can make the board incomplete
- Automatic play may violate the game terms of service

See `THIRD_PARTY_MORTAL.txt` and `THIRD_PARTY_AKOCHAN.txt` for third-party runtime notes.

## License

The original project is AGPL-3.0-or-later. See `LICENSE.md`.
