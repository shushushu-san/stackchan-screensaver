# スタックちゃんスクリーンセーバーを Windows に移植した

[bruiselea](https://github.com/bruiselea) さんが作った [stackchan-screensaver](https://github.com/bruiselea/stackchan-screensaver)（macOS 版）をフォークして、Windows でも動くようにした。

---

## きっかけ

bruiselea の macOS 版スクリーンセーバーを見て、Windows でも使いたいと思った。
macOS 版は Swift + `ScreenSaverView` で書かれており、そのままでは Windows に持っていけない。
フォークして Windows 版を追加することにした。

## 言語選定

macOS 版は Swift + `ScreenSaverView` で書かれている。Windows にはそのまま持っていけない。

選択肢を整理すると:

| 候補 | 理由 |
| --- | --- |
| **C# (WinForms)** | `.scr` ファイルは `.exe` の実体なので自然に作れる。GDI+ で円・矩形・多角形を描くだけなので移植が直感的 |
| Electron | README にも書いていたが、`.scr` として正式に動作させるのが難しい |
| C++ (Win32) | `WndProc` など低レベル実装が必要で移植コストが高すぎる |

C# / WinForms を選んだ。

## 移植のポイント

**座標系の変換が不要になった。**

macOS 版（`NSView`）は左下原点・Y 上向きなので、`py(y) = H - (offY + y*scale)` という Y 反転が必要だった。WinForms は左上原点・Y 下向きなので `py(y) = offY + y*scale` でそのまま使える。それ以外の座標数値（目の位置・サイズ・口の位置など）はすべて本家 m5stack-avatar の値をそのまま流用できた。

**API の対応:**

| macOS (Swift) | Windows (C#) |
| --- | --- |
| `IOKit` 電源情報 | `SystemInformation.PowerStatus` |
| `getloadavg` | `GetSystemTimes` P/Invoke で差分比率を算出 |
| `ProcessInfo.thermalState` | 相当する API なし（省略） |

## .scr の仕組み

Windows スクリーンセーバーは `.scr` という拡張子だが、実体はただの `.exe`。起動時の引数でモードが決まる:

- `/s` → 本番実行（フルスクリーン）
- `/p HWND` → 設定ダイアログ内のプレビュー
- `/c` または引数なし → 設定ダイアログ

`Program.cs` でこの引数を分岐し、`Form` を生成して渡すだけ。インストールは `build\StackchanSaver.scr`（`dotnet publish` で生成した `.exe` をリネームしたもの）を `System32` にコピーすれば完了。

## 構成

```text
windows-saver/
├── StackchanSaver.csproj
├── Program.cs            # 引数処理・モード分岐
├── ScreensaverForm.cs    # 描画・アニメーション・システム状態取得
├── build.ps1             # build.sh の PowerShell 版
└── build/
    └── debug/
        └── StackchanSaver.exe   # デバッグ用（dotnet build / build.ps1 run で生成）
```

`.scr` ファイルは `.\build.ps1 release` を実行したときに初めて `build\StackchanSaver.scr` として生成される（`dotnet publish` でシングルファイルにまとめた `.exe` をリネームしたもの）。現時点ではデバッグビルドまで確認済み。

## カメラ動体検知

スクリーンセーバー中にカメラを起動し、動きを検知してスタックちゃんを反応させることにした。

ライブラリは **OpenCvSharp4**（OpenCV の C# バインディング）を使った。`BackgroundSubtractorMOG2` で背景差分を取り、最大輪郭の重心を「動いているものの位置」として扱う。

検知した座標は `-1.0〜1.0` に正規化して渡す。カメラは 15fps・320×240 でバックグラウンドスレッドに閉じ込め、イベント経由で UI スレッドに通知する。

```csharp
// 動体の位置を正規化（左右反転を補正）
float normX = -(centroid.X / width  - 0.5f) * 2f;
float normY =  (centroid.Y / height - 0.5f) * 2f;
```

動体を検知すると **Surprised 表情**（目を 2 倍に拡大・四角い口）になり、視線がその方向を向く。カメラなし環境ではサイレントにスキップされる。

## 電子音声

動体を検知した瞬間、スタックちゃんが電子音で「しゃべる」ようにした。

最初は `kernel32.dll` の `Beep` API を使ったが、途切れ途切れになった。JIT がレジスタキャッシュするためポーリングがフリーズするなど、制御が難しいことがわかった。

**winmm.dll の `waveOut` API で PCM バッファを直接書き込む**方式に切り替えた。これにより矩形波をサンプル単位で合成でき、

- **スタッカート**: 短音を異なるピッチで並べたレトロロボット風（4 パターン）
- **グライド＋ビブラート**: ピッチをなめらかに変化させるシムシティ住民風（3 パターン）

の計 7 パターンをランダムに再生している。再生完了の待ち方は `dwFlags` ポーリングではなく `Thread.Sleep(サンプル数 × 1000 / SampleRate + 80ms)` で処理する。

```csharp
// 矩形波の基本ユニット（デューティ比 0.5 = 対称矩形波）
static void AppendSquare(List<short> buf, double hz, double durationSec, double duty = 0.5)
{
    int n = (int)(SampleRate * durationSec);
    double phase = 0, phaseInc = hz / SampleRate;
    for (int i = 0; i < n; i++)
    {
        buf.Add(phase < duty ? WaveAmp : (short)(-WaveAmp));
        phase += phaseInc;
        if (phase >= 1.0) phase -= 1.0;
    }
}
```

## 現状

- ビルド: `.\build.ps1 run` でデバッグ起動（`dotnet` を絶対パスで呼ぶ必要がある環境では `build.ps1` 内で対処済み）
- 顔アニメーション: 瞬き・呼吸・視線移動すべて動作
- 表情: 6 種類（Neutral / Happy / Angry / Sad / Sleepy / Surprised）
- システム連動: CPU 高負荷 / 充電中 / バッテリー低残量 / 長時間表示で表情変化
- カメラ動体検知: OpenCvSharp4 MOG2、動体に視線追従・Surprised 表情
- 電子音声: winmm waveOut PCM、7 パターンのランダム再生
- インストール: `.\build.ps1 open` で System32 へ配置 → 設定ダイアログを開く
