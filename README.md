# stackchan-screensaver

PC・Mac を放置（無操作）すると、スタックちゃん風の顔が全画面に出るスクリーンセーバーです。
顔は [meganetaaan/m5stack-avatar](https://github.com/meganetaaan/m5stack-avatar) (MIT) の
挙動・座標・表情ロジックを参考に再実装しています。

| プラットフォーム | 言語 | ドキュメント |
| --- | --- | --- |
| **macOS** | Swift + ScreenSaverView | [macos-saver/readme-macos.md](macos-saver/readme-macos.md) |
| **Windows** | C# / WinForms | [windows-saver/readme-windows.md](windows-saver/readme-windows.md) |

## 機能

- 瞬き / 呼吸 / 視線移動アニメーション
- PC / Mac の状態に応じた表情変化（CPU 高負荷 / 充電中 / バッテリー低残量 / 長時間表示）
- **Windows 版のみ**: カメラ動体検知 → 驚き表情 + 視線追従 + 電子音声発話

## ライセンス / クレジット

- 本リポジトリ: **MIT**（[`LICENSE`](LICENSE)）
- 顔の挙動・表情ロジックは **m5stack-avatar** by [@meganetaaan](https://github.com/meganetaaan)
  (Shinya Ishikawa, MIT, Copyright (c) 2018) を参考に再実装しています。
  詳細は [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)
- 「スタックちゃん / Stack-chan」の名称・キャラクターは [@meganetaaan](https://github.com/meganetaaan)
  氏のプロジェクト [stack-chan](https://github.com/meganetaaan/stack-chan) に由来します。
  本リポジトリは個人的な再実装であり、公式の製品ではありません。

---

## stackchan-screensaver (English)

A screen saver that shows a Stack-chan–style face full-screen when your PC or Mac is left idle.
Reimplemented from [meganetaaan/m5stack-avatar](https://github.com/meganetaaan/m5stack-avatar) (MIT).

| Platform | Language | Docs |
| --- | --- | --- |
| **macOS** | Swift + ScreenSaverView | [macos-saver/readme-macos.md](macos-saver/readme-macos.md) |
| **Windows** | C# / WinForms | [windows-saver/readme-windows.md](windows-saver/readme-windows.md) |

## Features

- Blink / breath / gaze animation
- Expression changes based on system state (high CPU / charging / low battery / long idle)
- **Windows only**: camera motion detection → Surprised expression + gaze tracking + electronic voice

## License / Credits

- This repository: **MIT** ([`LICENSE`](LICENSE))
- Face behavior / expression logic reimplemented from **m5stack-avatar** by
  [@meganetaaan](https://github.com/meganetaaan) (MIT). See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
- "Stack-chan / スタックちゃん" originates from [@meganetaaan](https://github.com/meganetaaan)'s
  [stack-chan](https://github.com/meganetaaan/stack-chan) project.
  This is a personal reimplementation and is **not** an official product.
