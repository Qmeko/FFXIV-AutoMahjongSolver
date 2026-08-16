# Doman Mahjong Solver Debug + Mortal AI

[English](README.md)

FFXIVのドマ式麻雀を読み取り、Mortal / Akochan の判断で打牌・鳴き・リーチ・和了を行う Dalamud プラグインです。

元プロジェクト: [XeldarAlz/FFXIV-DomanMahjongSolver](https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver)

## インストール

1. `/xlsettings` を実行し、**試験的機能**タブを開く
2. **カスタムプラグインリポジトリ** に次の URL を追加して有効化する

```
https://raw.githubusercontent.com/Qmeko/DalamudPlugins/refs/heads/main/pluginmaster.json
```

3. `/xlplugins` を実行し、**Doman Mahjong Solver Debug** をインストールする
4. プラグインを有効にする

ソースのビルドや `BUILD_DEBUG_PLUGIN.bat` は不要です。

## 初回セットアップ

- **Akochan** はプラグインに同梱されています
- **Mortal AI** は初回起動時に自動で導入されます（Python / PyTorch / 公開モデル。数分、数百MB）
- チャットに進行状況が出ます。失敗したら設定画面の **判断AI** から再試行できます

保存先:

```text
%LOCALAPPDATA%\DomanMahjongSolverDebug\MortalRuntime
```

## 使い方

1. `/mjdebug` で画面を開く
2. 起動直後は安全のため **Hints（提案だけ）** になります
3. 盤面と AI の接続を確認する
4. 自動操作したいときだけ、メイン画面で Auto-play をオンにする

設定の **判断AI** で Mortal と Akochan を切り替えられます。

## できること

- 打牌
- ポン / チー（候補形の選択含む）
- 暗槓 / 明槓 / 加槓
- リーチと宣言牌
- ロン / ツモ
- 鳴きの見送り

## コマンド

| コマンド | 内容 |
|---|---|
| `/mjdebug` | メイン画面を開閉 |

## 注意点

- Mortal は公開されているローカルモデルです。非公開の上位モデルと同じ強さではありません
- 相手の副露やリーチなど、ゲームから取れない情報があるときは判断が不完全になります
- 自動操作の利用にはゲーム規約上のリスクがあります

第三者ランタイムの説明は `THIRD_PARTY_MORTAL.txt` と `THIRD_PARTY_AKOCHAN.txt` を参照してください。

## License

元プロジェクトは AGPL-3.0-or-later です。`LICENSE.md` を参照してください。
