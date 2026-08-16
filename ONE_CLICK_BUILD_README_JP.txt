Doman Mahjong Solver Debug + Mortal AI - ワンクリックビルド

実行するファイル:
  BUILD_DEBUG_PLUGIN.bat

BATが自動で行うこと:
  1. インストール済み .NET 10 SDK を自動検出
  2. SDKが無ければプロジェクト内へローカル導入
  3. NuGet依存関係を短い専用パスへ復元
  4. Doman Mahjong Solver DebugをReleaseビルド
  5. 公開Mortalランタイム release4p.zip を取得
  6. uv、管理対象Python 3.12、CPU版PyTorch、NumPy、Requestsを導入
  7. Mortalモデルを実際に起動し、mjai JSONL往復テスト
  8. 開発者プラグイン登録用DLLをOUTPUTへ展開

初回操作:
  BUILD_DEBUG_PLUGIN.bat をダブルクリックします。
  初回は.NET、Python、PyTorch、Mortalを取得するためインターネット接続が必要です。
  管理者権限は不要です。

完成後:
  OUTPUT\DomanMahjongSolverDebug\DomanMahjongSolverDebug.dll
  OUTPUT\DEV_PLUGIN_DLL_PATH.txt
  OUTPUT\REGISTER_IN_DALAMUD.txt
  OUTPUT\MORTAL_READY.txt

Dalamud登録:
  /xlsettings
  → Experimental
  → Dev Plugin Locations
  → DEV_PLUGIN_DLL_PATH.txt に記載されたDLLを追加
  → /xlplugins で Doman Mahjong Solver Debug を有効化
  → /mjdebug

使用:
  Settings → Decision AI → Mortal AI (installed runtime)
  Main window → Auto-play

保存先:
  Mortal:
    %LOCALAPPDATA%\DomanMahjongSolverDebug\MortalRuntime
  NuGet:
    %LOCALAPPDATA%\DomanMahjongSolverDebug\NuGet

安全動作:
  Mortalが停止・タイムアウト・不正JSON・非合法手を返した場合、
  既定では内蔵AIへフォールバックします。
  デバッグ版は通常版とInternalName、コマンド、設定、ログが分離されています。

注意:
  公開release4pに含まれるローカルモデルの強さは、非公開・オンライン側の強いモデルと同一ではありません。
  FFXIV側で取得できない盤面情報がある局面ではMortalの判断精度が低下します。
