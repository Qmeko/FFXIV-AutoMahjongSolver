## v0.8.0.89

- Mortalランタイム配置時に、使用中のmortal.pthのモデル名・checkpoint・サイズ・SHA-256をMORTAL_MODEL_MANIFEST.jsonへ保存。
- Mortalプロセス起動時にmortal.pthを再ハッシュし、manifestと一致する場合だけ[MortalModel] status=VERIFIED_298Kを記録。
- モデル差替え、古いmanifest、欠落時はUNVERIFIED_OR_MISMATCH / NO_MANIFEST / MISSINGとして明示。

## v0.8.0.88

- 自家の河が先に増え、手牌が14/11/8/5/2枚のまま残る遷移を「次の自摸」と誤認しないよう修正。
- 同一自摸牌の二重送信によるAkochanの手牌枚数破損と `tehai_ana.cpp num <= 15` assertionを防止。
- 手牌縮小または実際の次自摸を確認するまで、自家打牌の構造反映待ちを保持。
- Akochanプロセスが応答なしで終了した場合を、遅延応答回収と誤記録しないよう修正。
- AI利用不可の内部Passセンチネルを、ユーザー向けの「パス」指示として表示しないよう修正。

## v0.8.0.83
- Akochan公式lightプリセットによるリアルタイム推論モードを追加。
- 従来探索は精度優先モードとして維持。
- OpenMPワーカーの動的縮退を無効化し、コア配置を固定。

## v0.8.0.82

- 自家の14枚手牌と相手河更新が同時に公開された場合でも、Akochan送信バッチの最後に自家`tsumo`を必ず配置するよう修正。
- 自家打牌局面の`none`応答を残留コール指示として保持しないよう修正。
- 古いコール判断を自家打牌局面へ持ち越さないよう修正。

## v0.8.0.81

- 自動操作を「選択送信」「AgentEmjでの選択／確定受信」「手牌・河・副露への構造反映」に分離した操作トランザクションへ変更。
- `state=30`やウィンドウ消失だけでは打牌成功と判定せず、手牌減少または自家河増加まで判断を保持。
- ポン／チーは手牌減少または副露増加まで保持し、複数チーのstate 25だけを同一トランザクションの継続として許可。
- リーチは一覧選択、`opcode 11`確定、候補打牌面、指定牌打牌を別段階として保持。
- 鳴き後の必須打牌を構造反映まで保持し、送信直後に破棄しない。
- 相手番の`legal=None`を停止状態として警告する誤判定を修正。
- Retry、固定Sleep、Stickyフラグは追加していません。

## v0.8.0.80

- 日本語クライアントのstate 6リーチ一覧を、`SelectItem`と次フレームの`opcode 11`確定通知による二段階プロトコルへ修正。
- リーチ確定通知は同一局面を検証した上で1回だけ送信し、再選択・Retry・固定Sleepを使用しない。
- 候補打牌面へ遷移後はAkochan指定牌を既存経路で1回だけ打牌。

# Doman Mahjong Solver Debug changelog

## 0.3.0.0

- Added one-click installation of the public four-player Mortal runtime.
- Added managed Python 3.12, CPU PyTorch and dependency setup through uv.
- Added real Mortal model/mjai round-trip verification during build.
- Corrected the external protocol to one JSON event array per input line.
- Added opening-hand reconstruction and incremental mjai event tracking.
- Added discard, riichi, win, pon, chi and kan action mapping with legality validation.
- Added exact chi-variant selection and built-in fallback.
- Preserved independent debug identity, settings, command and logs.

## 0.2.x

- Initial external-process bridge and debug-plugin packaging.
- Dalamud API 15 and Windows build-environment fixes.
