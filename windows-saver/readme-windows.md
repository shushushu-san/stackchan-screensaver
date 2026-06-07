# stackchan-screensaver (Windows 版)

Windows を放置（無操作）すると、スタックちゃん風の顔が全画面に出る **Windows スクリーンセーバー (`.scr`)** です。
顔は [meganetaaan/m5stack-avatar](https://github.com/meganetaaan/m5stack-avatar) (MIT) の
挙動・座標・表情ロジックを参考に C# / WinForms / GDI+ へ移植しています
（瞬き / 呼吸 / 視線移動）。

**PC の状態に応じて表情が変わります**。さらにカメラで動体を検知すると驚き顔になり、電子音で「しゃべり」ます。

| 状態（上が優先） | 表情 |
| --- | --- |
| カメラで動体を検知 | 驚き（目を 2 倍・四角い口・電子音発話） |
| CPU 負荷 > 70% | 怒り（目の上を斜め三角でカット） |
| 充電中（電源接続） | 嬉しい（◠ の目） |
| バッテリー残量 < 20% | 悲しい |
| 表示が 5 分超 | 眠い（半目） |
| それ以外 | 通常 |

## 動作環境

- **動作確認: Windows 11（x64）** — 作者の環境のみ。他バージョンは未検証
- **.NET 10 SDK** が必要（ビルド時のみ。インストール済みの `.scr` は不要）
- カメラ動体検知には **OpenCvSharp4** を使用（カメラなし環境ではサイレントにスキップ）

## インストール

### ビルドして System32 へ配置（1 コマンド）

`windows-saver/` フォルダで PowerShell を開き：

```powershell
.\build.ps1 open
```

1. `dotnet publish` でシングルファイル `.scr` をビルド
2. UAC ダイアログが出る → **「はい」** を押す（System32 へコピー）
3. スクリーンセーバー設定ダイアログが自動で開く
4. リストから **「StackchanSaver」** を選んで OK

### その他のビルドコマンド

| コマンド | 内容 |
| --- | --- |
| `.\build.ps1` | デバッグビルドのみ（`build\debug\StackchanSaver.exe`） |
| `.\build.ps1 run` | デバッグビルド → `/s` でフルスクリーン起動 |
| `.\build.ps1 release` | リリースビルドのみ（`build\StackchanSaver.scr`） |
| `.\build.ps1 install` | リリースビルド → System32 へコピー |
| `.\build.ps1 open` | install + スクリーンセーバー設定ダイアログを開く |

> PowerShell の仕様で `.\` が必要です（`build.ps1 run` ではなく `.\build.ps1 run`）。

### dotnet が PATH にない場合

`build.ps1` は `dotnet` が見つからない場合 `C:\Program Files\dotnet\dotnet.exe` を自動で使います。

## 仕組み（要点）

### 顔描画

仮想キャンバス 320×240（本家 m5stack-avatar の座標そのまま）を画面サイズにスケーリングして GDI+ で描画。
WinForms は左上原点・Y 下向きなので、macOS 版（左下原点・Y 上向き）と違い Y 反転が不要。

### スクリーンセーバーの引数仕様

`.scr` は実体がただの `.exe` で、起動引数でモードが切り替わる：

| 引数 | 動作 |
| --- | --- |
| `/s` | フルスクリーン実行 |
| `/p HWND` | 設定ダイアログ内プレビュー |
| `/c` または引数なし | 設定ダイアログ表示 |

### CPU 使用率

`kernel32.dll` の `GetSystemTimes` を P/Invoke で呼び出し、1 秒ごとにカーネル・ユーザー・アイドル時間の差分から算出。

### カメラ動体検知

`CameraMotionDetector.cs` がバックグラウンドスレッドで動く。
15fps / 320×240 でキャプチャし、OpenCvSharp4 の `BackgroundSubtractorMOG2` で背景差分を取る。
最大輪郭の重心を `-1.0〜1.0` に正規化して UI スレッドへイベント通知。

```csharp
MotionDetected(float normX, float normY)  // 動体あり
MotionLost()                              // 動体なし
```

### 電子音声

`winmm.dll` の `waveOut` API で 44100Hz 16bit モノラル PCM バッファを直接書き込む矩形波シンセ。
7 パターンをランダムに再生：

| パターン | 種類 |
| --- | --- |
| 1〜4 | スタッカート（短音を異なるピッチで並べたレトロロボット風） |
| 5〜7 | グライド＋ビブラート（ピッチをなめらかに変化させるシムシティ住民風） |

## ファイル構成

```text
windows-saver/
├── StackchanSaver.csproj   # .NET 10 WinForms プロジェクト
├── Program.cs              # 引数処理・モード分岐
├── ScreensaverForm.cs      # 描画・アニメーション・音声・システム状態取得
├── CameraMotionDetector.cs # OpenCvSharp4 MOG2 動体検知
├── build.ps1               # ビルドスクリプト
└── build/
    └── StackchanSaver.scr  # .\build.ps1 release で生成されるインストーラブル
```

## ライセンス / クレジット

- 本リポジトリ: **MIT**（[`../LICENSE`](../LICENSE)）
- 顔の挙動・表情ロジックは **m5stack-avatar** by [@meganetaaan](https://github.com/meganetaaan)
  (Shinya Ishikawa, MIT, Copyright (c) 2018) を参考に再実装しています。
  詳細は [`../THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md)
- 「スタックちゃん / Stack-chan」の名称・キャラクターは [@meganetaaan](https://github.com/meganetaaan)
  氏のプロジェクト [stack-chan](https://github.com/meganetaaan/stack-chan) に由来します。
  本リポジトリは個人的な再実装であり、公式の製品ではありません。

---

## stackchan-screensaver Windows version (English)

A **Windows screen saver (`.scr`)** that shows a Stack-chan–style face full-screen
when your PC is left idle. Ported from the macOS Swift version to C# / WinForms / GDI+,
based on the behavior, geometry, and expression logic of
[meganetaaan/m5stack-avatar](https://github.com/meganetaaan/m5stack-avatar) (MIT).

The face reacts to your PC's state. When the camera detects motion, it switches to a
Surprised expression and plays an electronic voice.

| State (top has priority) | Expression |
| --- | --- |
| Camera detects motion | Surprised (2× eyes, square mouth, electronic voice) |
| CPU load > 70% | Angry (diagonal triangle cut above eyes) |
| Charging (AC connected) | Happy (◠-shaped eyes) |
| Battery < 20% | Sad |
| Displayed for > 5 min | Sleepy (half-closed eyes) |
| Otherwise | Neutral |

## Requirements

- **Tested on Windows 11 (x64)** — author's environment only
- **.NET 10 SDK** required to build (not needed after installation)
- Camera motion detection uses **OpenCvSharp4** (silently skipped if no camera)

## Install

Open PowerShell inside the `windows-saver/` folder:

```powershell
.\build.ps1 open
```

This builds a single-file `.scr`, copies it to System32 (UAC prompt), and opens the
Screen Saver Settings dialog. Select **StackchanSaver** and click OK.

### Build commands

| Command | Action |
| --- | --- |
| `.\build.ps1` | Debug build only (`build\debug\StackchanSaver.exe`) |
| `.\build.ps1 run` | Debug build → launch fullscreen (`/s`) |
| `.\build.ps1 release` | Release build only (`build\StackchanSaver.scr`) |
| `.\build.ps1 install` | Release build → copy to System32 |
| `.\build.ps1 open` | install + open Screen Saver Settings |

> The `.\` prefix is required by PowerShell (use `.\build.ps1`, not `build.ps1`).

## License / Credits

- This repository: **MIT** ([`../LICENSE`](../LICENSE))
- Face behavior / expression logic reimplemented from **m5stack-avatar** by
  [@meganetaaan](https://github.com/meganetaaan) (MIT). See [`../THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).
- "Stack-chan / スタックちゃん" originates from [@meganetaaan](https://github.com/meganetaaan)'s
  [stack-chan](https://github.com/meganetaaan/stack-chan) project.
  This is a personal reimplementation and is **not** an official product.
