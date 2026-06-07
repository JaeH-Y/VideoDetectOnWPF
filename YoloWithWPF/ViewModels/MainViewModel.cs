using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing.Printing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using YoloWithWPF.Models;
using YoloWithWPF.Services;
using YoloWithWPF.Views;

namespace YoloWithWPF.ViewModels
{
    class MainViewModel : ObservableObject
    {

        private readonly YoloService _yoloService;
        private readonly SemaphoreSlim _detectionSemaphore = new SemaphoreSlim(1, 1);

        private BitmapSource? _currentFrame;
        public BitmapSource CurrentFrame
        {
            get => _currentFrame;
            set => SetProperty(ref _currentFrame, value);
        }

        public ObservableCollection<CameraStreamItem> CameraList { get; } = new ObservableCollection<CameraStreamItem>();

        // CameraList에서 클릭으로 선택 된 항목
        private CameraStreamItem? _highlightedCamera;
        public CameraStreamItem? HighlightedCamera
        {
            get => _highlightedCamera;
            set => SetProperty(ref _highlightedCamera, value);
        }

        // 메인 영상 표시용
        private CameraStreamItem _selectedCamera;
        public CameraStreamItem SelectedCamera
        {
            get => _selectedCamera;
            set
            {
                if (value != _selectedCamera)
                {
                    SetProperty(ref _selectedCamera, value);
                }
            }
        }

        private string _detectionResult = "탐지 없음";
        public string DetectionResult
        {
            get => _detectionResult;
            set => SetProperty(ref _detectionResult, value);
        }

        private string _currentResolution = "-";
        public string CurrentResolution
        {
            get => _currentResolution;
            set
            {
                SetProperty(ref _currentResolution, value);
                Debug.WriteLine(value);

            }
        }

        private string _currentFps = "-";
        public string CurrentFps
        {
            get => _currentFps;
            set
            {
                SetProperty(ref _currentFps, value);
                Debug.WriteLine(value);

            }
        }

        private string _infoMessage = "준비 완료";
        public string InfoMessage
        {
            get => _infoMessage;
            set => SetProperty(ref _infoMessage, value);
        }

        private List<(OpenCvSharp.Rect box, float conf, string label)> _detections;
        public List<(OpenCvSharp.Rect box, float conf, string label)> Detections
        {
            get => _detections;
            set => SetProperty(ref _detections, value);
        }

        public ICommand OpenVideoCommand { get; }
        public ICommand StartCameraCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand AddRtspCommand { get; }

        public ICommand SelectCameraCommand { get; }

        public MainViewModel()
        {
            _yoloService = new YoloService(@"C:\Workspace\c#\wpf\YoloWithWPF\YoloWithWPF\new_best.onnx");

            AddRtspCommand = new AsyncRelayCommand(AddRtsp);
            SelectCameraCommand = new RelayCommand<CameraStreamItem>(SelectCamera);
        }

        private async Task AddRtsp()
        {
            var dialog = new RtspDialog()
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() != true) return;

            var input = dialog.ResultText;
            if (string.IsNullOrWhiteSpace(input)) return;

            // 파일 경로인지 RTSP인지 구분
            bool isFile = System.IO.File.Exists(input);
            bool isRtsp = input.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);

            if (!isFile && !isRtsp)
            {
                InfoMessage = "유효하지 않은 경로 또는 RTSP 주소입니다.";
                return;
            }

            var name = isFile
                ? System.IO.Path.GetFileNameWithoutExtension(input)
                : input.Replace("rtsp://", ""); // RTSP는 호스트 부분을 이름으로

            var item = new CameraStreamItem(
                name: name,
                rtspUrl: input,
                resolution: "-"
            );

            Action<CameraService, Mat> thumbnailHandler = (service, frame) => UpdateThumbnail(service, item, frame);
            item.Service.OnThumbnailReady += thumbnailHandler;
            Action<double, double, double> infoHandler = (w, h, fps) => CameraInfoHandler(item, w, h, fps);
            item.Service.OnCameraInfoReady += infoHandler;
            if (CameraList.Count == 0)
                SelectCamera(item);

            bool result = await item.Service.StartVideoAsync(input);  // 파일/RTSP 모두 동일 메서드로 처리
            if (result)
            {
                CameraList.Add(item);
                InfoMessage = $"{item.Name} 추가됨";
            }
            else
            {
                item.Service.OnThumbnailReady -= thumbnailHandler;
                item.Service.OnCameraInfoReady -= infoHandler;
                if (_selectedCamera == item)
                {
                    _selectedCamera.Service.OnFrameReady -= OnFrameReady;
                    _selectedCamera = null;
                }
                InfoMessage = $"{item.Name} 추가 실패";
            }
        }

        private void SelectCamera(CameraStreamItem? item)
        {
            if (item == null) return;

            // 이전 Selected 구독 해제
            if (_selectedCamera != null)
            {
                _selectedCamera.Service.OnFrameReady -= OnFrameReady;
            }

            // 새 Selected 구독
            item.Service.OnFrameReady += OnFrameReady;

            SelectedCamera = item;
            CurrentResolution = item.Resolution;
            CurrentFps = item.Fps.ToString("F1");
            InfoMessage = $"{item.Name} 선택됨";
        }

        private void UpdateThumbnail(CameraService service, CameraStreamItem item, Mat frame)
        {
            if (!ReferenceEquals(service, item.Service))
            {
                frame.Dispose();
                return;
            }
            // 썸네일은 저화질로 리사이즈
            using(frame)
            using (Mat small = new Mat())
            {
                Cv2.Resize(frame, small, new OpenCvSharp.Size(240, 135));

                var bitmap = BitmapSourceConverter.ToBitmapSource(small);
                bitmap.Freeze();

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    item.Thumbnail = bitmap;
                });

            }
        }

        private void OnFrameReady(CameraService sender, Mat frame)
        {

            try
            {
                if (_selectedCamera == null)
                    return;

                // 새로 들어오는 프레임이 현재 선택된 카메라의 프레임이 아니라면 무시
                if (!ReferenceEquals(sender, _selectedCamera.Service))
                    return;

                // 바운딩 박스 그리기
                if (Detections != null)
                {
                    foreach (var (box, conf, label) in Detections)
                    {
                        Cv2.Rectangle(frame, box, Scalar.Red, 2);
                        Cv2.PutText(frame, $"{label} {conf:F2}",
                            new OpenCvSharp.Point(box.X, box.Y - 5),
                            HersheyFonts.HersheySimplex, 0.6, Scalar.Red, 2);
                    }
                }

                // 화면 표시는 추론과 관계없이 진행
                var bitmap = BitmapSourceConverter.ToBitmapSource(frame);
                bitmap.Freeze();

                // UI 스레드 호출
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    // 미리 던져 놓은 프레임이 현재 선택된 카메라의 프레임인지 재검증 후 업데이트
                    if (ReferenceEquals(sender, _selectedCamera?.Service))
                        CurrentFrame = bitmap;
                });

                if (!_detectionSemaphore.Wait(0))
                {
                    // 이전 탐지가 아직 처리 중이면 이번 프레임은 건너뜀
                    return;
                }

                var detectionFrame = frame.Clone(); // 탐지용 프레임 복제
                _ = DetectAsync(sender, detectionFrame);


            }
            finally
            {
                // 프레임이 더 이상 필요 없으므로 메모리 해제
                frame.Dispose();
            }
        }

        private async Task DetectAsync(CameraService sender, Mat frame)
        {
            try
            {
                var detections = await Task.Run(() => _yoloService.Detect(frame));

                // 추론 중 카메라가 변경됐을 수 있으므로 재검증
                if (!ReferenceEquals(sender, _selectedCamera?.Service)) return;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Detections = detections;
                    DetectionResult = Detections.Count > 0
                        ? string.Join("\n", Detections.Select(d => $"{d.label} {d.conf:F2}"))
                        : "탐지 없음";
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"YOLO 추론 실패: {ex.Message}");
            }
            finally
            {
                frame.Dispose();
                _detectionSemaphore.Release();
            }
        }

        private void CameraInfoHandler(CameraStreamItem item, double width, double height, double fps)
        {
            item.Resolution = $"{(int)width}x{(int)height}";
            item.Fps = fps;

            if (ReferenceEquals(item, SelectedCamera))
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    CurrentResolution = item.Resolution;
                    CurrentFps = $"{item.Fps:F1} fps";
                });
            }
        }
    }
}
