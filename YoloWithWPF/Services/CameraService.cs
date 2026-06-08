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
        public readonly int MaxReconnectAttempts = 3;
        private ConnectStatusEnum _currentStatus = ConnectStatusEnum.Disconnected;

        private Stopwatch? _frameStopwatch;

        public event Action<CameraService, Mat>? OnFrameReady;
        public event Action<CameraService, Mat>? OnThumbnailReady;
        public event Action<double, double, double>? OnCameraInfoReady; // width, height, fps
        public event Action<CameraService, ConnectStatusEnum>? OnCameraStatus;
        public event Action<CameraService, int>? OnReconnectCount;

        public async Task<bool> StartVideoAsync(string videoPath)
        {
            await StopAsync();
            return await Task.Run(() => StartVideo(videoPath));
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

                while (!token.IsCancellationRequested)
                {
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        _frameStopwatch = _frameStopwatch ?? Stopwatch.StartNew();
                        OnCameraStatus?.Invoke(this, ConnectStatusEnum.FrameReceiveStopped);
                        
                        if (_frameStopwatch.Elapsed >= TimeSpan.FromSeconds(5))
                        {
                            OnCameraStatus?.Invoke(this, ConnectStatusEnum.Disconnected);
                            _ = TryReconnect();
                            _frameStopwatch.Stop();
                            _frameStopwatch = null; // 재시도 후 타이머 초기화
                            break;
                        }

                        token.WaitHandle.WaitOne(1000); // 1초 대기 후 재시도
                        continue;
                    }

                    if(_currentStatus != ConnectStatusEnum.Connected)
                    {
                        OnCameraStatus?.Invoke(this, ConnectStatusEnum.Connected);
                        _currentStatus = ConnectStatusEnum.Connected;
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

        public async Task TryReconnect()
        {
            if (string.IsNullOrEmpty(_currentPath))
                return;

            OnCameraStatus?.Invoke(this, ConnectStatusEnum.Reconnecting);
            // 루프 해제 후 재시도
            for (int i = 1; i <= MaxReconnectAttempts; i++)
            {
                OnReconnectCount?.Invoke(this, i);
                bool reconnect = await StartVideoAsync(_currentPath);
                if (reconnect)
                {
                    OnCameraStatus?.Invoke(this, ConnectStatusEnum.Connecting);
                    if(_frameStopwatch != null)
                    {
                        _frameStopwatch.Stop();
                        _frameStopwatch = null;
                    }
                    return;
                }
                await Task.Delay(1000); // 재시도 간격
            }
            OnCameraStatus?.Invoke(this, ConnectStatusEnum.AutoReconnectFailed);
        }   

        public async Task StopAsync()
        {
            CancellationTokenSource? cts = _cts;
            Task? task = _captureTask;

            _cts = null;
            _captureTask = null;
            _capture = null;

            if (cts == null)
            {
                return;
            }


            try
            {
                cts.Cancel();
                if (task != null)
                    await task;
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
            }
        }
    }
}
