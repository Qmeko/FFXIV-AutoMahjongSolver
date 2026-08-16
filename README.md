# Doman Mahjong Solver Debug + Mortal AI

[English](README.en.md)

FFXIVのドマ式麻雀を読み取り、Mortal AIの判断を使って打牌・鳴き・リーチ・和了操作を行う、通常版とは分離されたDalamud開発者プラグインです。

元プロジェクト: [XeldarAlz/FFXIV-DomanMahjongSolver](https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver)

## カスタムリポジトリからインストール

1. `/xlsettings` を実行し、**試験的機能**タブを開く
2. **カスタムプラグインリポジトリ** に次の URL を追加する:

```
https://raw.githubusercontent.com/Qmeko/DalamudPlugins/refs/heads/main/pluginmaster.json
```

3. `/xlplugins` を実行し、**ドマ式麻雀ソルバー デバッグ版** をインストールする

## ワンクリック構築

1. ZIPを短いパスへ展開します。
2. `BUILD_DEBUG_PLUGIN.bat`をダブルクリックします。
3. BATが次を自動実行します。
   - 利用可能な.NET 10 SDKの選択
   - NuGet依存関係の復元
   - `DomanMahjongSolverDebug.dll`のReleaseビルド
   - Mortal 4人麻雀アダプターの取得
   - uv管理のPython 3.12環境の作成
   - CPU版PyTorchと必要パッケージの導入
   - 実際のMortalモデルを使ったJSONL往復テスト
4. 成功後、`OUTPUT\DEV_PLUGIN_DLL_PATH.txt`に書かれたDLLをDalamudのDev Plugin Locationsへ登録します。

生成物:

```text
OUTPUT\DomanMahjongSolverDebug\DomanMahjongSolverDebug.dll
OUTPUT\DomanMahjongSolverDebug-latest.zip
OUTPUT\MORTAL_READY.txt
OUTPUT\DEV_PLUGIN_DLL_PATH.txt
```

Mortalランタイムは次へ独立配置されます。

```text
%LOCALAPPDATA%\DomanMahjongSolverDebug\MortalRuntime
```

## Dalamudへの登録

```text
/xlsettings
→ Experimental
→ Dev Plugin Locations
→ OUTPUT\DEV_PLUGIN_DLL_PATH.txt のDLLを追加
```

`/xlplugins`で **Doman Mahjong Solver Debug** を有効化し、`/mjdebug`で画面を開きます。

デバッグ版は起動時に安全のためHints状態へ戻ります。盤面表示とMortal接続状態を確認した後、メイン画面でAuto-playを有効にします。

## 自動処理

- 打牌
- ポン
- チーと候補形選択
- 暗槓
- 明槓
- 加槓
- リーチと宣言牌
- ロン
- ツモ
- 鳴きの見送り
- AI異常時の内蔵ヒューリスティックへのフォールバック

Mortalとの通信は、1行につきmjaiイベント配列を1つ送信し、1行のアクションを受信するJSONL方式です。返答はFFXIV側の合法手と照合してから実行します。

## コマンド

| コマンド | 内容 |
|---|---|
| `/mjdebug` | メイン画面を開閉 |
| `/mjdebug pass <N>` | 鳴き候補の指定位置をデバッグ送信 |
| `/mjdebug capture <label>` | 次のコールバックを記録 |
| `/mjdebug variant dump` | クライアントレイアウト情報を出力 |
| `/mjdebug snap <label>` | 現在の盤面スナップショットを保存 |
| `/mjdebug autosnap on` | 状態変化の自動記録を開始 |
| `/mjdebug autosnap off` | 自動記録を停止 |

## 注意点

- Mortal公開リリースのローカルモデルを使用します。上位の非公開モデルと同等の強さではありません。
- JP表示の鳴き・リーチ・ツモ・ロン文字列も認識対象へ追加しています。
- FFXIVから取得できない相手副露・相手リーチなどの情報がある場合、Mortalへ渡る盤面は不完全になります。
- 通常版とは内部名、DLL名、コマンド、設定、ログを分離しています。
- 外部ツールと自動操作の利用にはゲーム規約上のリスクがあります。

詳細は`ONE_CLICK_BUILD_README_JP.txt`、`MODIFICATION_SUMMARY.md`、`THIRD_PARTY_MORTAL.txt`を参照してください。

## License

元プロジェクトはAGPL-3.0-or-laterです。`LICENSE.md`を参照してください。
