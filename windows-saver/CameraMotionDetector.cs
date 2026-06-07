// CameraMotionDetector.cs
// バックグラウンドスレッドでカメラを取得し、MOG2 背景差分で動体を検知する。
// 動体領域の重心を正規化座標 (-1.0〜+1.0) に変換してイベントで通知する。

using System;
using System.Threading;
using OpenCvSharp;

namespace StackchanSaver;

sealed class CameraMotionDetector : IDisposable
{
    // 動体検知時: normX/normY は -1.0〜+1.0 (左上が -1,-1、右下が +1,+1)
    public event Action<float, float> MotionDetected;

    // 動体消失時
    public event Action MotionLost;

    // ── 設定 ─────────────────────────────────────────────────────────────────

    // 前景マスク上で「動体あり」とみなす最小面積 (px^2, カメラの縮小後サイズ基準)
    const double MinMotionArea = 500.0;

    // カメラ処理の fps (CPU 負荷を抑えるため低めに設定)
    const int CaptureFps = 15;

    // ── 内部状態 ─────────────────────────────────────────────────────────────

    Thread  _thread;
    volatile bool _running;
    volatile bool _hasMotion;

    // ── 公開 API ──────────────────────────────────────────────────────────────

    public void Start()
    {
        _running = true;
        _thread  = new Thread(Loop) { IsBackground = true, Name = "CameraDetector" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
    }

    // ── メインループ（バックグラウンドスレッド） ─────────────────────────────

    void Loop()
    {
        try
        {
            using var cap = new VideoCapture(0, VideoCaptureAPIs.DSHOW);

            if (!cap.IsOpened()) return;

            cap.Set(VideoCaptureProperties.FrameWidth,  320);
            cap.Set(VideoCaptureProperties.FrameHeight, 240);
            cap.Set(VideoCaptureProperties.Fps, CaptureFps);

            using var subtractor = BackgroundSubtractorMOG2.Create(
                history:       500,
                varThreshold:  40,
                detectShadows: false);

            using var frame   = new Mat();
            using var gray    = new Mat();
            using var fgMask  = new Mat();
            using var blurred = new Mat();

            int intervalMs = 1000 / CaptureFps;

            while (_running)
            {
                long start = Environment.TickCount64;

                if (!cap.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(intervalMs);
                    continue;
                }

                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, blurred, new OpenCvSharp.Size(5, 5), 0);
                subtractor.Apply(blurred, fgMask);

                Cv2.FindContours(
                    fgMask,
                    out OpenCvSharp.Point[][] contours,
                    out _,
                    RetrievalModes.External,
                    ContourApproximationModes.ApproxSimple);

                double bestArea = 0;
                OpenCvSharp.Point2f centroid = default;

                foreach (var c in contours)
                {
                    double area = Cv2.ContourArea(c);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        var m = Cv2.Moments(c);
                        if (m.M00 > 0)
                            centroid = new OpenCvSharp.Point2f(
                                (float)(m.M10 / m.M00), (float)(m.M01 / m.M00));
                    }
                }

                if (bestArea >= MinMotionArea)
                {
                    float normX = -((centroid.X / frame.Width  - 0.5f) * 2f);
                    float normY =   (centroid.Y / frame.Height - 0.5f) * 2f;
                    _hasMotion = true;
                    MotionDetected?.Invoke(normX, normY);
                }
                else if (_hasMotion)
                {
                    _hasMotion = false;
                    MotionLost?.Invoke();
                }

                int elapsed = (int)(Environment.TickCount64 - start);
                int wait    = Math.Max(0, intervalMs - elapsed);
                if (wait > 0) Thread.Sleep(wait);
            }
        }
        catch (Exception)
        {
            // OpenCV ネイティブ DLL が読み込めない場合などはサイレントに終了
        }
    }

    public void Dispose() => Stop();
}
