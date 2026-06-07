// ScreensaverForm.cs
// Windows スクリーンセーバー本体。
// 本家 meganetaaan/m5stack-avatar (MIT) の顔ロジックを
// macOS 版 StackchanSaverView.swift からさらに C# / WinForms / GDI+ へ移植。
//
// 座標系の注意:
//   Swift (NSView) は左下原点・Y 上向き → py(y) = H - (offY + y*scale) と Y 反転が必要だった。
//   C# (WinForms)  は左上原点・Y 下向き → py(y) = offY + y*scale で変換不要。
//   それ以外の座標・数値はすべて Swift 版と同一。
//
// システム状態取得:
//   CPU 負荷: GetSystemTimes (kernel32) — カーネル/ユーザー時間の差分比率
//   バッテリー: SystemInformation.PowerStatus (WinForms 組み込み)
//   発熱 (thermalState): Windows には直接対応 API がないため省略

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows.Forms;

namespace StackchanSaver;

sealed class ScreensaverForm : Form
{
    // ── Win32 P/Invoke ────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    static extern IntPtr SetParent(IntPtr hChild, IntPtr hParent);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME
    {
        public uint Low, High;
        public long ToLong() => ((long)High << 32) | Low;
    }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    const int GWL_STYLE  = -16;
    const int WS_CHILD   = 0x40000000;
    const int WS_VISIBLE = 0x10000000;

    // ── 仮想キャンバス（本家 M5Stack 320×240 / Swift 版と同一数値） ───────────

    const float Vw         = 320f;
    const float Vh         = 240f;
    const float Cx         = 160f;   // Vw / 2
    const float EyeR       = 8f;
    const float EyeSpread  = 70f;
    const float EyeY       = 94f;
    const float MouthY     = 148f;
    const float MaxW       = 90f;
    const float MinH       = 4f;
    const double SleepyAfterSec = 300.0;   // 5 分以上表示 → 眠い

    enum Expr { Neutral, Happy, Angry, Sad, Sleepy }

    // ── アニメーション状態 ───────────────────────────────────────────────────

    double _t;
    double _nextBlink, _blinkUntil, _nextGaze;
    float  _gx, _gy, _tgx, _tgy;
    double _prevTime;
    double _sampleAccum;

    readonly Stopwatch _sw  = Stopwatch.StartNew();
    readonly Random    _rng = new Random();

    // ── システム状態 ─────────────────────────────────────────────────────────

    Expr   _expr      = Expr.Neutral;
    double _cpuLoad   = 0.0;
    bool   _isCharging;
    float  _battery   = 1f;

    // GetSystemTimes 前回値（差分で CPU 使用率を算出）
    long _prevIdle, _prevKernel, _prevUser;

    // ── GDI+ ブラシ（フレームごとに生成しないようキャッシュ） ────────────────

    readonly SolidBrush _white = new SolidBrush(Color.White);
    readonly SolidBrush _black = new SolidBrush(Color.Black);

    // ── モード ───────────────────────────────────────────────────────────────

    readonly bool   _isPreview;
    readonly IntPtr _previewHandle;

    // マウス移動による終了: 最初の 1 回は無視（起動直後の誤検知を防ぐ）
    Point _lastMouse = new Point(-1, -1);

    System.Windows.Forms.Timer _animTimer;

    // ── コンストラクタ: フルスクリーン ────────────────────────────────────────

    public ScreensaverForm(Rectangle screenBounds)
    {
        _isPreview = false;
        InitForm();
        FormBorderStyle = FormBorderStyle.None;
        Bounds          = screenBounds;
        TopMost         = true;
        Cursor.Hide();
        StartAnimation();
    }

    // ── コンストラクタ: 設定ダイアログ内プレビュー ───────────────────────────

    public ScreensaverForm(IntPtr previewHandle)
    {
        _isPreview     = true;
        _previewHandle = previewHandle;
        InitForm();
        FormBorderStyle = FormBorderStyle.None;
        StartAnimation();
    }

    // ── 共通初期化 ────────────────────────────────────────────────────────────

    void InitForm()
    {
        Text           = "StackchanSaver";
        BackColor      = Color.Black;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint            |
            ControlStyles.OptimizedDoubleBuffer,
            true);
    }

    void StartAnimation()
    {
        // CPU 前回値を初期化（次の SampleCpu 呼び出しで正確な差分が取れる）
        if (GetSystemTimes(out var idle0, out var kern0, out var usr0))
        {
            _prevIdle   = idle0.ToLong();
            _prevKernel = kern0.ToLong();
            _prevUser   = usr0.ToLong();
        }

        SamplePower();
        UpdateExpr();
        ScheduleBlink();
        ScheduleGaze();
        _prevTime = _sw.Elapsed.TotalSeconds;

        _animTimer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60fps
        _animTimer.Tick += (_, _) => Tick();
        _animTimer.Start();
    }

    // ── プレビューパネルへの埋め込み ─────────────────────────────────────────
    // OnHandleCreated はウィンドウハンドルが生成された直後に呼ばれる。
    // ここで SetParent を使い、設定ダイアログのプレビューパネルを親にする。

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (!_isPreview || _previewHandle == IntPtr.Zero) return;

        SetParent(Handle, _previewHandle);

        // WS_CHILD を追加して正しく子ウィンドウとして機能させる
        int style = GetWindowLong(Handle, GWL_STYLE);
        SetWindowLong(Handle, GWL_STYLE, style | WS_CHILD | WS_VISIBLE);

        GetClientRect(_previewHandle, out RECT r);
        SetBounds(0, 0, r.Right, r.Bottom);
    }

    // ── アニメーションティック ──────────────────────────────────────────────

    void Tick()
    {
        double now = _sw.Elapsed.TotalSeconds;
        double dt  = Math.Min(now - _prevTime, 0.1); // 最大 0.1 秒でクランプ
        _prevTime      = now;
        _t            += dt;
        _sampleAccum  += dt;

        // 1 秒ごとにシステム状態をサンプリング
        if (_sampleAccum >= 1.0)
        {
            _sampleAccum = 0;
            SampleCpu();
            SamplePower();
            UpdateExpr();
        }

        // 瞬き
        if (_t >= _nextBlink && _t >= _blinkUntil)
        {
            _blinkUntil = _t + 0.12;
            ScheduleBlink();
        }

        // 視線移動ターゲットを更新
        if (_t >= _nextGaze)
        {
            _tgx = (float)(_rng.NextDouble() * 2.0 - 1.0);
            _tgy = (float)(_rng.NextDouble() * 2.0 - 1.0);
            ScheduleGaze();
        }

        // 視線を目標に向けてなめらかに補間
        float k = Math.Min(1f, (float)dt * 4f);
        _gx += (_tgx - _gx) * k;
        _gy += (_tgy - _gy) * k;

        Invalidate();
    }

    void ScheduleBlink() => _nextBlink = _t + 2.0 + _rng.NextDouble() * 4.0;
    void ScheduleGaze()  => _nextGaze  = _t + 2.5 + _rng.NextDouble() * 4.0;

    // ── 描画 ────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        Draw(g);
    }

    void Draw(Graphics g)
    {
        float W = ClientSize.Width, H = ClientSize.Height;
        if (W <= 0 || H <= 0) return;

        // 仮想 320×240 をアスペクト比を保ってセンタリング
        float sc   = Math.Min(W / Vw, H / Vh);
        float offX = (W - Vw * sc) / 2f;
        float offY = (H - Vh * sc) / 2f;

        // 仮想座標 → ウィンドウ座標
        // WinForms は Y 下向きなので Swift 版の Y 反転は不要
        float Px(float x) => offX + x * sc;
        float Py(float y) => offY + y * sc;

        // ── 描画ヘルパー（Swift の vCircle / vRect / vTriangle に対応） ───

        void Circle(float cx, float cy, float r, SolidBrush b)
        {
            float s = r * sc;
            g.FillEllipse(b, Px(cx) - s, Py(cy) - s, s * 2f, s * 2f);
        }

        void Rect(float x0, float y0, float w, float h, SolidBrush b)
            => g.FillRectangle(b, Px(x0), Py(y0), w * sc, h * sc);

        void Triangle(float ax, float ay, float bx, float by, float cx, float cy, SolidBrush br)
            => g.FillPolygon(br, new[]
            {
                new PointF(Px(ax), Py(ay)),
                new PointF(Px(bx), Py(by)),
                new PointF(Px(cx), Py(cy)),
            });

        // ── 背景 ──────────────────────────────────────────────────────────

        g.Clear(Color.Black);

        bool  open = _t >= _blinkUntil;
        float goX  = _gx * 3f;
        float goY  = _gy * 3f;

        // ── 目の描画（本家 Eye.cpp の draw() をそのまま移植） ─────────────
        // isLeft: false = 画面左（本家「右目」相当）
        //         true  = 画面右（本家「左目」相当）

        void DrawEye(float ex, bool isLeft)
        {
            float exc = ex + goX;
            float eyc = EyeY + goY;
            float r   = EyeR;

            if (!open)
            {
                // 瞬き: 目を横棒に置き換え
                Rect(exc - r, eyc - 2f, r * 2f, 4f, _white);
                return;
            }

            // ベースの白丸
            Circle(exc, eyc, r, _white);

            if (_expr == Expr.Angry || _expr == Expr.Sad)
            {
                // 目の上部を斜め三角でカット → 怒り・悲しみ表情
                // 怒り: 内側(中央寄り)の上角をカット
                // 悲しみ: 外側の上角をカット（怒りの左右反転）
                float x0   = exc - r, y0 = eyc - r, x1 = exc + r;
                bool  cond = (!isLeft) != !(_expr == Expr.Sad);
                float x2   = cond ? x0 : x1;
                Triangle(x0, y0, x1, y0, x2, eyc, _black);
            }

            if (_expr == Expr.Happy || _expr == Expr.Sleepy)
            {
                // 目の下半分を矩形でマスク
                float y0 = eyc - r;
                float x0 = exc - r, w = r * 2f + 4f, h = r + 2f;

                if (_expr == Expr.Happy)
                {
                    // Happy: さらに内側を黒丸で抜いて ◠ 形に
                    y0 += r;                         // マスク開始位置を中心に下げる
                    Circle(exc, eyc, r / 1.5f, _black);
                }

                Rect(x0, y0, w, h, _black);
            }
        }

        DrawEye(Cx - EyeSpread, false);   // 画面左（本家「右目」）
        DrawEye(Cx + EyeSpread, true);    // 画面右（本家「左目」）

        // ── 口（呼吸アニメーション ±2px） ────────────────────────────────

        float breath = (float)Math.Sin(_t * 1.6) * 2f;
        Rect(Cx - MaxW / 2f, MouthY - MinH / 2f + breath, MaxW, MinH, _white);
    }

    // ── システム状態取得 ─────────────────────────────────────────────────────

    void SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kern, out var usr)) return;

        long i = idle.ToLong(), k = kern.ToLong(), u = usr.ToLong();

        long di = i - _prevIdle;
        long dk = k - _prevKernel;
        long du = u - _prevUser;

        _prevIdle   = i;
        _prevKernel = k;
        _prevUser   = u;

        long total = dk + du;
        if (total == 0) return;

        // Windows: カーネル時間にはアイドル時間も含まれる
        _cpuLoad = Math.Max(0.0, (double)(total - di) / total);
    }

    void SamplePower()
    {
        var ps = SystemInformation.PowerStatus;

        // デスクトップ PC などバッテリーなしの場合は充電・残量の判定をスキップ
        bool hasBattery = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0;

        _isCharging = hasBattery && ps.PowerLineStatus == PowerLineStatus.Online;

        float pct = ps.BatteryLifePercent; // 0.0–1.0、不明 (255/100=2.55) の場合もある
        _battery   = (hasBattery && pct >= 0f && pct <= 1f) ? pct : 1f;
    }

    void UpdateExpr()
    {
        if      (_cpuLoad > 0.7)       _expr = Expr.Angry;    // CPU 高負荷
        else if (_isCharging)          _expr = Expr.Happy;    // 充電中
        else if (_battery < 0.2f)      _expr = Expr.Sad;      // バッテリー残量僅少
        else if (_t > SleepyAfterSec)  _expr = Expr.Sleepy;   // 長時間表示
        else                           _expr = Expr.Neutral;
    }

    // ── 入力処理: 何か操作があればスクリーンセーバーを終了 ───────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_isPreview) return;

        // 起動直後に Windows が送る MouseMove イベントで即終了しないよう
        // 最初の 1 回だけ位置を記録して無視する
        if (_lastMouse.X < 0)
        {
            _lastMouse = e.Location;
            return;
        }

        // 3px 以上動いたら終了
        if (Math.Abs(e.X - _lastMouse.X) > 3 || Math.Abs(e.Y - _lastMouse.Y) > 3)
            ExitSaver();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!_isPreview) ExitSaver();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_isPreview) ExitSaver();
    }

    static void ExitSaver()
    {
        Cursor.Show();
        Application.Exit();
    }

    // ── リソース解放 ─────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animTimer?.Dispose();
            _white.Dispose();
            _black.Dispose();
        }
        base.Dispose(disposing);
    }
}
