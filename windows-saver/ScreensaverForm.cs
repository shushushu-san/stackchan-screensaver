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

    // ── winmm P/Invoke ────────────────────────────────────────────────────────

    const int WAVE_FORMAT_PCM = 1;
    const int CALLBACK_NULL   = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint   nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR
    {
        public IntPtr lpData;
        public uint   dwBufferLength, dwBytesRecorded, dwUser, dwFlags, dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hWaveOut);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hWaveOut);

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

    enum Expr { Neutral, Happy, Angry, Sad, Sleepy, Surprised }

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

    // ── カメラ動体検知 ────────────────────────────────────────────────────────

    CameraMotionDetector _camera;
    // Surprised 表情の自動解除時刻 (0 = 非アクティブ)
    double _surprisedUntil;
    // カメラ検知で上書きする視線ターゲット (null = 使わない)
    float? _camTgx, _camTgy;
    // 最後に発話した時刻（初期値は -∞ 扱いにするため十分負にする）
    double _lastTalkedAt = -999;

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

        // カメラ動体検知を開始（カメラなし環境ではサイレントにスキップ）
        _camera = new CameraMotionDetector();
        _camera.MotionDetected += OnMotionDetected;
        _camera.MotionLost     += OnMotionLost;
        _camera.Start();
    }

    // ── カメライベントハンドラー（バックグラウンドスレッドから呼ばれる） ────

    void OnMotionDetected(float normX, float normY)
    {
        // UI スレッドへマーシャリング
        if (IsDisposed) return;
        try
        {
            Invoke(() =>
            {
                _camTgx         = normX;
                _camTgy         = normY;
                _surprisedUntil = _t + 2.0;   // 2 秒間 Surprised 表情を維持
            });
        }
        catch (ObjectDisposedException) { }
    }

    void OnMotionLost()
    {
        if (IsDisposed) return;
        try { Invoke(() => { _camTgx = null; _camTgy = null; }); }
        catch (ObjectDisposedException) { }
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

        // Surprised 表情: カメラ検知中かつ維持期間内
        if (_camTgx.HasValue || _t < _surprisedUntil)
            _expr = Expr.Surprised;

        // Surprised 中で、前回発話から 4 秒以上経過していたら発話
        if (_expr == Expr.Surprised && _t - _lastTalkedAt >= 4.0)
        {
            _lastTalkedAt = _t;
            Task.Run(PlayTalkSound).ContinueWith(
                t => { /* 例外を静かに無視 */ },
                TaskContinuationOptions.OnlyOnFaulted);
        }

        // 瞬き
        if (_t >= _nextBlink && _t >= _blinkUntil)
        {
            _blinkUntil = _t + 0.12;
            ScheduleBlink();
        }

        // 視線移動ターゲットを更新
        // カメラ検知中はカメラの重心方向を優先する
        if (_camTgx.HasValue)
        {
            _tgx = _camTgx.Value;
            _tgy = _camTgy.Value;
        }
        else if (_t >= _nextGaze)
        {
            _tgx = (float)(_rng.NextDouble() * 2.0 - 1.0);
            _tgy = (float)(_rng.NextDouble() * 2.0 - 1.0);
            ScheduleGaze();
        }

        // 視線を目標に向けてなめらかに補間
        // カメラ追跡中は指数「8」で滑らかに追従、通常は「4」
        float trackSpeed = _camTgx.HasValue ? 8f : 4f;
        float k = Math.Min(1f, (float)dt * trackSpeed);
        _gx += (_tgx - _gx) * k;
        _gy += (_tgy - _gy) * k;

        Invalidate();
    }

    void ScheduleBlink() => _nextBlink = _t + 2.0 + _rng.NextDouble() * 4.0;
    void ScheduleGaze()  => _nextGaze  = _t + 2.5 + _rng.NextDouble() * 4.0;

    // ── 電子音で話しかける（waveOut PCM 矩形波） ──────────────────────────────

    const int   SampleRate = 44100;
    const short WaveAmp    = 8000;

    static void AppendSquare(List<short> buf, double hz, double durationSec, double duty = 0.5)
    {
        int    n        = (int)(SampleRate * durationSec);
        double phase    = 0;
        double phaseInc = hz / SampleRate;
        for (int i = 0; i < n; i++)
        {
            buf.Add(phase < duty ? WaveAmp : (short)(-WaveAmp));
            phase += phaseInc;
            if (phase >= 1.0) phase -= 1.0;
        }
    }

    static void AppendGlide(List<short> buf, double fromHz, double toHz, double durationSec, double duty = 0.5)
    {
        int    n     = (int)(SampleRate * durationSec);
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t  = (double)i / n;
            double hz = fromHz + (toHz - fromHz) * t;
            buf.Add(phase < duty ? WaveAmp : (short)(-WaveAmp));
            phase += hz / SampleRate;
            if (phase >= 1.0) phase -= 1.0;
        }
    }

    static void AppendVibrato(List<short> buf, double hz, double durationSec,
                              double vibratoRate = 6.0, double vibratoDepth = 12.0, double duty = 0.5)
    {
        int    n     = (int)(SampleRate * durationSec);
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t      = (double)i / SampleRate;
            double instHz = hz + vibratoDepth * Math.Sin(2 * Math.PI * vibratoRate * t);
            buf.Add(phase < duty ? WaveAmp : (short)(-WaveAmp));
            phase += instHz / SampleRate;
            if (phase >= 1.0) phase -= 1.0;
        }
    }

    static void AppendSilence(List<short> buf, double durationSec)
    {
        int n = (int)(SampleRate * durationSec);
        for (int i = 0; i < n; i++) buf.Add(0);
    }

    // ── 発話パターン 1〜4: スタッカート ──────────────────────────────────────

    static short[] TalkPopo()
    {
        var b = new List<short>();
        AppendSquare(b, 600,  0.07);
        AppendSilence(b, 0.025);
        AppendSquare(b, 800,  0.07);
        AppendSilence(b, 0.025);
        AppendSquare(b, 500,  0.09);
        return b.ToArray();
    }

    static short[] TalkPipopo()
    {
        var b = new List<short>();
        AppendSquare(b, 700,  0.065);
        AppendSilence(b, 0.022);
        AppendSquare(b, 1000, 0.055);
        AppendSilence(b, 0.022);
        AppendSquare(b, 650,  0.065);
        AppendSilence(b, 0.022);
        AppendSquare(b, 750,  0.085);
        return b.ToArray();
    }

    static short[] TalkPipapopo()
    {
        var b = new List<short>();
        AppendSquare(b, 600,  0.065);
        AppendSilence(b, 0.020);
        AppendSquare(b, 950,  0.055);
        AppendSilence(b, 0.020);
        AppendSquare(b, 800,  0.065);
        AppendSilence(b, 0.020);
        AppendSquare(b, 550,  0.070);
        AppendSilence(b, 0.020);
        AppendSquare(b, 700,  0.085);
        return b.ToArray();
    }

    static short[] TalkPopopipapopo()
    {
        var b = new List<short>();
        AppendSquare(b, 650,  0.060);
        AppendSilence(b, 0.018);
        AppendSquare(b, 900,  0.055);
        AppendSilence(b, 0.018);
        AppendSquare(b, 600,  0.060);
        AppendSilence(b, 0.018);
        AppendSquare(b, 700,  0.065);
        AppendSilence(b, 0.018);
        AppendSquare(b, 600,  0.060);
        AppendSilence(b, 0.018);
        AppendSquare(b, 1000, 0.055);
        AppendSilence(b, 0.018);
        AppendSquare(b, 750,  0.065);
        AppendSilence(b, 0.018);
        AppendSquare(b, 550,  0.095);
        return b.ToArray();
    }

    // ── 発話パターン 5〜7: グライド＋ビブラート ──────────────────────────────

    static short[] TalkDore()
    {
        var b = new List<short>();
        AppendGlide(b,   500, 660, 0.06);
        AppendVibrato(b, 660,      0.07, 6, 15);
        AppendGlide(b,   660, 490, 0.04);
        AppendGlide(b,   490, 710, 0.065);
        AppendVibrato(b, 710,      0.08, 7, 20);
        AppendGlide(b,   710, 860, 0.07);
        AppendVibrato(b, 860,      0.07, 8, 25);
        return b.ToArray();
    }

    static short[] TalkKyui()
    {
        var b = new List<short>();
        AppendGlide(b,   580, 1040, 0.055);
        AppendVibrato(b, 1040,       0.055, 8, 30);
        AppendGlide(b,   1040, 1220, 0.04);
        AppendSilence(b, 0.022);
        AppendGlide(b,   1220,  760, 0.075);
        AppendVibrato(b, 760,        0.07, 6, 16);
        return b.ToArray();
    }

    static short[] TalkUwa()
    {
        var b = new List<short>();
        AppendGlide(b,   360, 550, 0.08);
        AppendVibrato(b, 550,      0.06, 5, 12);
        AppendGlide(b,   550, 720, 0.09);
        AppendVibrato(b, 720,      0.13, 6, 24);
        AppendGlide(b,   720, 600, 0.06);
        AppendVibrato(b, 600,      0.08, 7, 18);
        AppendGlide(b,   600, 400, 0.10);
        return b.ToArray();
    }

    static readonly Func<short[]>[] TalkBuilders =
    [
        TalkPopo, TalkPipopo, TalkPipapopo, TalkPopopipapopo,
        TalkDore, TalkKyui, TalkUwa,
    ];

    void PlayTalkSound()
    {
        var samples = TalkBuilders[_rng.Next(TalkBuilders.Length)]();
        PlaySamples(samples);
    }

    static void PlaySamples(short[] samples)
    {
        var fmt = new WAVEFORMATEX
        {
            wFormatTag      = WAVE_FORMAT_PCM,
            nChannels       = 1,
            nSamplesPerSec  = SampleRate,
            wBitsPerSample  = 16,
            nBlockAlign     = 2,
            nAvgBytesPerSec = SampleRate * 2,
        };

        if (waveOutOpen(out var hWave, 0xFFFFFFFF, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL) != 0)
            return;

        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        var gch = GCHandle.Alloc(bytes, GCHandleType.Pinned);

        var hdr = new WAVEHDR
        {
            lpData         = gch.AddrOfPinnedObject(),
            dwBufferLength = (uint)bytes.Length,
        };
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();

        waveOutPrepareHeader(hWave, ref hdr, hdrSize);
        waveOutWrite(hWave, ref hdr, hdrSize);

        int waitMs = (int)(samples.Length * 1000.0 / SampleRate) + 80;
        Thread.Sleep(waitMs);

        waveOutReset(hWave);
        waveOutUnprepareHeader(hWave, ref hdr, hdrSize);
        gch.Free();
        waveOutClose(hWave);
    }

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
        // カメラ追跡中は視線の振れ幅を拡大（15px）、通常は本家準拠（3px）
        float gazeRange = _camTgx.HasValue ? 15f : 3f;
        float goX  = _gx * gazeRange;
        float goY  = _gy * gazeRange;

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

            // Surprised: 目を大きく描く（白丸のみ）
            float drawR = (_expr == Expr.Surprised) ? r * 2.0f : r;

            // ベースの白丸
            Circle(exc, eyc, drawR, _white);

            if (_expr == Expr.Surprised) return;

            if (_expr == Expr.Angry || _expr == Expr.Sad)
            {
                // 目の上部を斜め三角でカット → 怒り・悲しみ表情
                // 怒り: 内側(中央寄り)の上角をカット
                // 悲しみ: 外側の上角をカット（怒りの左右反転）
                float x0   = exc - drawR, y0 = eyc - drawR, x1 = exc + drawR;
                bool  cond = (!isLeft) != !(_expr == Expr.Sad);
                float x2   = cond ? x0 : x1;
                Triangle(x0, y0, x1, y0, x2, eyc, _black);
            }

            if (_expr == Expr.Happy || _expr == Expr.Sleepy)
            {
                // 目の下半分を矩形でマスク
                float y0 = eyc - drawR;
                float x0 = exc - drawR, w = drawR * 2f + 4f, h = drawR + 2f;

                if (_expr == Expr.Happy)
                {
                    // Happy: さらに内側を黒丸で抜いて ◠ 形に
                    y0 += drawR;                         // マスク開始位置を中心に下げる
                    Circle(exc, eyc, drawR / 1.5f, _black);
                }

                Rect(x0, y0, w, h, _black);
            }
        }

        DrawEye(Cx - EyeSpread, false);   // 画面左（本家「右目」）
        DrawEye(Cx + EyeSpread, true);    // 画面右（本家「左目」）

        // ── 口（Surprised のときは丸口、通常は呼吸アニメーション ±2px） ─

        if (_expr == Expr.Surprised)
        {
            // 驚きの「口」: 白い四角口
            float mw = 28f, mh = 22f;
            Rect(Cx - mw / 2f, MouthY - mh / 2f, mw, mh, _white);
        }
        else
        {
            float breath = (float)Math.Sin(_t * 1.6) * 2f;
            Rect(Cx - MaxW / 2f, MouthY - MinH / 2f + breath, MaxW, MinH, _white);
        }
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
        // Surprised は Tick() 内でリアルタイムに設定されるため UpdateExpr では扱わない
        if      (_t < _surprisedUntil)  return;                // 検知直後は維持
        if      (_cpuLoad > 0.7)        _expr = Expr.Angry;   // CPU 高負荷
        else if (_isCharging)           _expr = Expr.Happy;   // 充電中
        else if (_battery < 0.2f)       _expr = Expr.Sad;     // バッテリー残量僅少
        else if (_t > SleepyAfterSec)   _expr = Expr.Sleepy;  // 長時間表示
        else                            _expr = Expr.Neutral;
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
            _camera?.Dispose();
            _animTimer?.Dispose();
            _white.Dispose();
            _black.Dispose();
        }
        base.Dispose(disposing);
    }
}
