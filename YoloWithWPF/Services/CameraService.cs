using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using YoloWithWPF.Enums;

namespace YoloWithWPF.Services
{
    public class CameraService
    {
        private VideoCapture? _capture;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private int _frameIndex = 0;
        private string? _currentPath;
        public bool IsFile;
        public readonly int MaxReconnectAttempts = 3;
        private ConnectStatusEnum _currentStatus = ConnectStatusEnum.Disconnected;
        private readonly SemaphoreSlim _statusLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private PeriodicTimer? _frameCheckTimer;
        private Task? _timerTask;

        private Stopwatch? _frameStopwatch;

        public event Action<CameraService, Mat>? OnFrameReady;
        public event Action<CameraService, Mat>? OnThumbnailReady;
        public event Action<double, double, double>? OnCameraInfoReady; // width, height, fps
        public event Action<CameraService, ConnectStatusEnum>? OnCameraStatus;
        public event Action<CameraService, int>? OnReconnectCount;

        public async Task<CameraOperationResult> StartVideoAsync(string videoPath)
        {
            if (!await _connectLock.WaitAsync(0))
                return CameraOperationResult.Busy;

            try
            {
                await StopAsync();
                bool started = await Task.Run(() => StartVideo(videoPath));
                return started
                    ? CameraOperationResult.Success
                    : CameraOperationResult.Failed;
            }
            finally
            {
                _connectLock.Release();
            }
        }

        public void StartCamera(int cameraIndex = 0)
        {
            _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.FFMPEG);
            double width = _capture.Get(VideoCaptureProperties.FrameWidth);
            double height = _capture.Get(VideoCaptureProperties.FrameHeight);
            double fps = _capture.Get(VideoCaptureProperties.Fps);
            OnCameraInfoReady?.Invoke(width, height, fps);

            //Start();
        }

        public bool StartVideo(string videoPath)
        {
            VideoCapture? capture = null;
            try
            {
                capture = new VideoCapture(videoPath, VideoCaptureAPIs.FFMPEG);
                if (!capture.IsOpened())
                {
                    throw new Exception("영상 파일을 열 수 없습니다.");
                }

                _currentPath = videoPath;
                double width = capture.Get(VideoCaptureProperties.FrameWidth);
                double height = capture.Get(VideoCaptureProperties.FrameHeight);
                double fps = capture.Get(VideoCaptureProperties.Fps);

                VideoCapture runningCapture = capture; // CaptureLoop에 전달할 캡처 객체

                _capture = runningCapture; // 성공적으로 열렸을 때만 할당
                _cts = new CancellationTokenSource();
                _frameIndex = 0;

                OnCameraInfoReady?.Invoke(width, height, fps);

                _captureTask = Task.Run(() => CaptureLoop(runningCapture, _cts.Token));
                if (!IsFile)
                {
                    _timerTask = StartFrameChecker(_cts.Token);
                }

                // CaptureLoop에 전달 완료, 이제 로컬 변수는 필요 없으므로 null로 설정하여 Dispose 방지
                capture = null; 

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"영상 시작 실패: {ex.Message}");
                return false;
            }
            finally
            {
                // CaptureLoop에 전달되지 못한 경우만 정리
                capture?.Release();
                capture?.Dispose();
            }
        }

        private void CaptureLoop(VideoCapture capture, CancellationToken token)
        {
            try
            {
                using Mat frame = new Mat();

                double fps = capture.Get(VideoCaptureProperties.Fps);
                int delay = fps > 0 ? (int)(1000 / fps) : 33; // fps 못 읽으면 30fps로 fallback

                _frameStopwatch = _frameStopwatch ?? Stopwatch.StartNew();

                while (!token.IsCancellationRequested)
                {
                    bool readSuccess = capture.Read(frame);

                    if (token.IsCancellationRequested) break;

                    if (!readSuccess || frame.Empty())
                    {
                        if (IsFile)
                        {
                            SetCameraStatus(ConnectStatusEnum.FileStreamDone);
                            break;
                        }

                        SetCameraStatus(ConnectStatusEnum.FrameReceiveStopped);

                        if (token.WaitHandle.WaitOne(1000)) break;

                        continue;
                    }

                    _frameStopwatch.Restart();

                    if(_currentStatus != ConnectStatusEnum.Connected)
                    {
                        SetCameraStatus(ConnectStatusEnum.Connected);
                    }
                    OnFrameReady?.Invoke(this, frame.Clone());
                    if (_frameIndex++ % 3 == 0)
                        OnThumbnailReady?.Invoke(this, frame.Clone());
                    
                    if(token.WaitHandle.WaitOne(delay))
                        break;
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"캡처 루프 중 오류: {ex.Message}");
            }
            finally
            {
                // 자원 정리
                capture.Release();
                capture.Dispose();
            }
        }

        private async Task StartFrameChecker(CancellationToken token)
        {
            if(_frameCheckTimer == null)
            {
                _frameCheckTimer = new(TimeSpan.FromSeconds(1));
            }
            try
            {
                bool timedOut = false;
                while (await _frameCheckTimer.WaitForNextTickAsync(token))
                {
                    if (_frameStopwatch?.Elapsed.TotalSeconds > 5)
                    {
                        timedOut = true;
                        break;
                    }
                    else if(_frameStopwatch?.Elapsed.TotalSeconds >= 1)
                    {
                        SetCameraStatus(ConnectStatusEnum.FrameReceiveStopped);
                    }
                }
                if (!timedOut || IsFile) return;
                
                SetCameraStatus(ConnectStatusEnum.Disconnected);
                _ = TryReconnect(); // 데드락 방지
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Frame 수신 체크 중 오류 발생: {ex.Message}");
            }
        }

        public async Task<CameraOperationResult> TryReconnect()
        {
            if (!await _statusLock.WaitAsync(0))
                return CameraOperationResult.Busy;

            try
            {
                if (string.IsNullOrEmpty(_currentPath))
                    return CameraOperationResult.Failed;

                SetCameraStatus(ConnectStatusEnum.Reconnecting);

                for (int i = 1; i <= MaxReconnectAttempts; i++)
                {
                    OnReconnectCount?.Invoke(this, i);
                    CameraOperationResult reconnect = await StartVideoAsync(_currentPath);

                    if (reconnect == CameraOperationResult.Success)
                    {
                        SetCameraStatus(ConnectStatusEnum.Connecting);
                        if (_frameStopwatch != null)
                        {
                            _frameStopwatch.Stop();
                            _frameStopwatch = null;
                        }
                        return CameraOperationResult.Success;
                    }

                    if (reconnect == CameraOperationResult.Busy)
                        return CameraOperationResult.Busy;

                    await Task.Delay(1000);
                }
                OnReconnectCount?.Invoke(this, MaxReconnectAttempts+1);
                SetCameraStatus(ConnectStatusEnum.AutoReconnectFailed);
                return CameraOperationResult.Failed;
            }
            finally
            {
                _statusLock.Release();
            }
        }   

        private void SetCameraStatus(ConnectStatusEnum status)
        {
            if(_currentStatus == status) return;
            _currentStatus = status;
            OnCameraStatus?.Invoke(this, _currentStatus);
        }

        public async Task<CameraOperationResult> DeleteCamera()
        {
            if (!await _statusLock.WaitAsync(0))
                return CameraOperationResult.Busy;

            try
            {
                await StopAsync();
                return CameraOperationResult.Success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"카메라 삭제 실패: {ex.Message}");
                return CameraOperationResult.Failed;
            }
            finally
            {
                _statusLock.Release();
            }
        }

        public async Task StopAsync()
        {
            CancellationTokenSource? cts = _cts;
            Task? task = _captureTask;
            Task? timerTask = _timerTask;

            _cts = null;
            _captureTask = null;
            _timerTask = null;
            _capture = null;

            if (cts == null)
            {
                return;
            }


            try
            {
                cts.Cancel();
                if (task != null)
                    await Task.WhenAny(task, Task.Delay(2000));
                if(timerTask != null)
                    _ = timerTask;  // 데드락 방지
            }
            catch (OperationCanceledException)
            {
                // 정상적으로 취소된 경우 예외 무시
            }
            catch (Exception ex)
            {
                Console.WriteLine($"캡처 작업 종료 중 오류: {ex.Message}");
            }
            finally
            {
                cts.Dispose();
                _frameCheckTimer?.Dispose();
                _frameCheckTimer = null;
                _frameStopwatch = null;
            }
        }
    }
}
