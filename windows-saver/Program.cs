// Program.cs
// Windows スクリーンセーバー (.scr) エントリポイント。
// .scr ファイルはコマンドライン引数でモードが決まる:
//   /s または -s  : スクリーンセーバー実行（全画面）
//   /p HWND       : 設定ダイアログ内のプレビューパネルに埋め込み
//   /c [HWND]     : 設定ダイアログ表示
//   引数なし      : /c と同じ

using System;
using System.Windows.Forms;

namespace StackchanSaver;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // 未処理例外をメッセージボックスで表示（デバッグ用）
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "StackchanSaver Error");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show(e.ExceptionObject.ToString(), "StackchanSaver Fatal");

        if (args.Length == 0)
        {
            ShowConfig();
            return;
        }

        string flag = args[0].TrimStart('-', '/').ToUpperInvariant();

        switch (flag)
        {
            case "S":
                RunFullscreen();
                break;

            case "P" when args.Length > 1:
                RunPreview(new IntPtr(long.Parse(args[1])));
                break;

            default:
                ShowConfig();
                break;
        }
    }

    // ── フルスクリーン（全モニター対応） ─────────────────────────────────────
    static void RunFullscreen()
    {
        var screens = Screen.AllScreens;
        var forms   = new Form[screens.Length];

        for (int i = 0; i < screens.Length; i++)
            forms[i] = new ScreensaverForm(screens[i].Bounds);

        // プライマリ以外を先に表示しておく（同一メッセージループで動く）
        for (int i = 1; i < forms.Length; i++)
            forms[i].Show();

        Application.Run(forms[0]);
    }

    // ── 設定ダイアログ内プレビュー ────────────────────────────────────────────
    static void RunPreview(IntPtr previewHandle)
    {
        Application.Run(new ScreensaverForm(previewHandle));
    }

    // ── 設定ダイアログ（設定項目なし、説明を表示） ───────────────────────────
    static void ShowConfig()
    {
        MessageBox.Show(
            "StackchanSaver\n\n" +
            "スタックちゃんスクリーンセーバー — 設定項目なし\n\n" +
            "CPU 負荷 > 70%      → 怒り顔\n" +
            "充電中               → 嬉しい顔\n" +
            "バッテリー残量 < 20% → 悲しい顔\n" +
            "5 分以上表示         → 眠い顔\n" +
            "それ以外             → 通常",
            "StackchanSaver",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
