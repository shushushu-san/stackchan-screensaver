# stackchan-screensaver

Mac を放置(無操作)すると、スタックちゃん風の顔が全画面に出るスクリーンセーバです。
顔は [meganetaaan/m5stack-avatar](https://github.com/meganetaaan/m5stack-avatar) (MIT) の
挙動・座標・表情ロジックを参考に再実装しています（瞬き / 呼吸 / 視線移動）。

さらに、**Mac の状態に応じて表情が変わります**（許可ダイアログ不要の範囲で取得）。

| 状態（上が優先） | 表情 |
|------|------|
| CPU 負荷が高い / 発熱 | 怒り（目の上を斜め三角でカット） |
| 充電中（電源接続） | 嬉しい（◠ の目） |
| バッテリー < 20% | 悲しい |
| 表示が長い（既定 5 分超） | 眠い（半目） |
| それ以外 | 通常 |

## 2 つの実装

| | 形式 | 用途 |
|---|---|---|
| **macOS 公式 .saver**（本命） | `ScreenSaverView` (Swift) | OS のスクリーンセーバ枠に登録。アイドル判定・全画面・復帰は OS 任せ |
| Electron 版 | Electron + Canvas | クロスプラットフォームの常駐アプリ。開発・確認用 / 自前配布向き |

### .saver 版（推奨・Xcode 不要、すべて CLI）

```bash
cd macos-saver
./build.sh open      # ビルド → ~/Library/Screen Savers へ導入 → 設定を開く
```

- `./build.sh preview` … NSWindow で即プレビュー（`n`通常 / `h`嬉しい / `a`怒り / `d`悲しい / `s`眠い / `space`自動）
- `./build.sh install` … ビルドして導入のみ
- 詳細は [`macos-saver/README.md`](macos-saver/README.md)

`swiftc` で arm64 + x86_64 のユニバーサルバイナリを `.saver` バンドルとして組み立て、
ad-hoc 署名まで自動で行います。

### Electron 版

```bash
npm install
npm start
```

3 分（既定）無操作で顔が全画面表示され、マウス/キー入力で消えます。
構成: `main.js`（アイドル監視 + 全画面ウィンドウ）/ `preload.js` / `renderer/`（顔描画）。
顔の色・サイズは `renderer/face.js` 先頭の `FACE` で調整できます。

## これから

- [ ] 配布版（Developer ID 署名 + notarization）
- [ ] App Store 版（`.saver` を同梱するマネージャアプリ）
- [ ] 設定 UI（しきい値・色）

## ライセンス / クレジット

- 本リポジトリ: **MIT**（[`LICENSE`](LICENSE)）
- 顔の挙動・表情ロジックは **m5stack-avatar (MIT, Copyright (c) 2018 Shinya Ishikawa)** を
  参考に再実装。詳細は [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)
- 「スタックちゃん / Stack-chan」の名称・キャラクターは ししかわ (Shinya Ishikawa) 氏の
  プロジェクトに由来します。本リポジトリは個人的な再実装であり、公式の製品ではありません。
