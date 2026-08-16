# Doman Mahjong Solver Debug + Mortal AI

[日本語](README.md)

A separate Dalamud developer plugin that reads Doman Mahjong and uses Mortal / Akochan for discard, calls, riichi, and win actions.

Original project: [XeldarAlz/FFXIV-DomanMahjongSolver](https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver)

## Install from the custom repository

1. Run `/xlsettings` and open the **Experimental** tab
2. Add this URL under **Custom Plugin Repositories**:

```
https://raw.githubusercontent.com/Qmeko/DalamudPlugins/refs/heads/main/pluginmaster.json
```

3. Run `/xlplugins` and install **ドマ式麻雀ソルバー デバッグ版**

On first launch, missing Mortal AI files are downloaded automatically (Python / PyTorch / the public model). Akochan is already inside the plugin zip.

## Commands

| Command | Description |
| --- | --- |
| `/mjdebug` | Toggle the main window |

See [README.md](README.md) for the full Japanese documentation and one-click build steps.
