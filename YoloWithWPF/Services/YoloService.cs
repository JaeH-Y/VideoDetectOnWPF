using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace YoloWithWPF.Services
{
    class YoloService : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly InferenceCsvLogger _performanceLogger;
        private readonly string[] _classNames = { "drone", "bird", "plane" };
        private readonly string[] _targetClassNames = { "drone" };
        private const float ConfThreshold = 0.5f;
        private const float IouThreshold = 0.5f;
        private readonly int _imgSize;
        private readonly float[] _inputBuffer;
        private readonly DenseTensor<float> _inputTensor;
        private readonly string _inputName;
        private readonly int[] _inputShape;
        private readonly object _detectLock = new();

        public bool UseGPU { get; private set; } = false;

        public YoloService(string modelPath, int imgSize = 1280)
        {
            _imgSize = imgSize;
            _performanceLogger = new InferenceCsvLogger();

            try
            {
                var options = new SessionOptions();
                //options.AppendExecutionProvider_Tensorrt(0);
                options.AppendExecutionProvider_CUDA(0);

                _session = new InferenceSession(modelPath, options);

                UseGPU = true;
                Debug.WriteLine("GPU 연결 성공");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("CUDA 실패, CPU로 실행");
                Debug.WriteLine(ex.ToString());

                var cpuOptions = new SessionOptions();
                cpuOptions.AppendExecutionProvider_CPU();

                _session = new InferenceSession(modelPath, cpuOptions);
            }
            finally
            {
                _inputShape = new[] { 1, 3, _imgSize, _imgSize };
                _inputBuffer = new float[3 * _imgSize * _imgSize];
                _inputTensor = new DenseTensor<float>(_inputBuffer, _inputShape);
                _inputName = _session!.InputMetadata.Keys.First();
            }
        }

        public List<(Rect box, float conf, string label)> Detect(Mat frame)
        {
            lock (_detectLock)
            {
                return DetectCore(frame);
            }
        }

        public List<(Rect box, float conf, string label)> DetectCore(Mat frame)
        {
            int originH = frame.Height;
            int originW = frame.Width;
            var totalWatch = Stopwatch.StartNew();

            // 전처리
            var stageWatch = Stopwatch.StartNew();
            //using Mat rgb = new Mat();
            //Preprocess(frame, rgb);
            //FillTensorFromRgb(rgb);
            using Mat resized = new Mat();
            PreprocessV2(frame, resized);
            FillTensorFromBgr(resized);
            stageWatch.Stop();
            double preprocessMs = stageWatch.Elapsed.TotalMilliseconds;

            // 추론 실행
            stageWatch.Restart();
            using var results = RunInference();
            stageWatch.Stop();
            double inferenceMs = stageWatch.Elapsed.TotalMilliseconds;

            // 후처리
            stageWatch.Restart();
            var detections = Postprocess(results, originH, originW);
            stageWatch.Stop();
            double postprocessMs = stageWatch.Elapsed.TotalMilliseconds;

            totalWatch.Stop();
            _performanceLogger.Write(
                preprocessMs,
                inferenceMs,
                postprocessMs,
                totalWatch.Elapsed.TotalMilliseconds);

            return detections;
        }

        private void Preprocess(Mat frame, Mat rgb)
        {
            // resize + BGR->RGB
            using Mat resized = new Mat();
            Cv2.Resize(frame, resized, new Size(_imgSize, _imgSize));
            Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);
        }
        private void PreprocessV2(Mat frame, Mat resized)
        {
            // resize
            Cv2.Resize(frame, resized, new Size(_imgSize, _imgSize));
        }

        private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunInference()
        {
            // 추론 세팅
            // session.InputMetadata.Keys.First() → 모델 입력 이름인 "images" 를 가져와요
            // NamedOnnxValue → "이 이름의 입력에 이 tensor를 넣겠다" 는 묶음이에요
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName, _inputTensor)
            };
            return _session.Run(inputs);
        }

        private List<(Rect box, float conf, string label)> Postprocess(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
            int originH, int originW)
        {
            // 추론 결과
            var output = results.First().AsTensor<float>();

            // 후처리
            // csharpoutput.Dimensions[0] // 1      → 배치
            // output.Dimensions[1] // 7      → cx, cy, w, h + 클래스 수
            // output.Dimensions[2] // 33600  → 후보 박스 수
            int numBoxes = output.Dimensions[2];
            int numClasses = output.Dimensions[1] - 4;
            // [1, 7, 33600] -> 탐지 결과 추출
            var detections = new List<(Rect, float, string)>();

            for (int i = 0; i < numBoxes; i++)
            {
                float maxConf = 0f;
                int classId = -1;
                // output의 구조가 [배치, 채널, 박스번호] 3차원 배열이에요.
                for (int c = 0; c < numClasses; c++)
                {
                    // 0 → 배치 1장짜리라 항상 0
                    // 4 + c → 채널 인덱스. c=0이면 drone(4번), c=1이면 bird(5번), c=2이면 plane(6번)
                    // i → 지금 보고 있는 박스 번호(0~33599)
                    float conf = output[0, 4 + c, i];
                    if (conf > maxConf) { maxConf = conf; classId = c; }
                }

                if (maxConf < ConfThreshold) continue;

                if (!_targetClassNames.Contains(_classNames[classId])) continue;

                // cx, cy, w, h -> 원본 이미지 좌표로 변환
                // 0 → 배치, 항상 0
                // 3 → 채널 인덱스. 채널 순서가 cx(0), cy(1), w(2), h(3), drone(4), bird(5), plane(6) 이라서 3번이 h
                // i → 지금 보고 있는 박스 번호
                float cx = output[0, 0, i] / _imgSize * originW;
                float cy = output[0, 1, i] / _imgSize * originH;
                float w = output[0, 2, i] / _imgSize * originW;
                float h = output[0, 3, i] / _imgSize * originH;

                int x1 = (int)(cx - w / 2);
                int y1 = (int)(cy - h / 2);

                detections.Add((new Rect(x1, y1, (int)w, (int)h), maxConf, _classNames[classId]));
            }

            return ApplyNMS(detections);
        }

        private unsafe void FillTensorFromRgb(Mat rgb)
        {
            byte* ptr = (byte*)rgb.DataPointer;
            int area = _imgSize * _imgSize;

            for (int i = 0; i < area; i++)
            {
                int src = i * 3;

                _inputBuffer[i] = ptr[src] / 255f;              // R
                _inputBuffer[i + area] = ptr[src + 1] / 255f;   // G
                _inputBuffer[i + area * 2] = ptr[src + 2] / 255f;// B
            }
        }

        // Convert 생략 -> BGR->RGB 변환과 동시에 텐서 채우기
        private unsafe void FillTensorFromBgr(Mat rgb)
        {
            byte* ptr = (byte*)rgb.DataPointer;
            int area = _imgSize * _imgSize;
            float scale = 1f / 255f;

            for (int i = 0; i < area; i++)
            {
                int src = i * 3;

                _inputBuffer[i] = ptr[src+2] * scale;               // R
                _inputBuffer[i + area] = ptr[src + 1] * scale;      // G
                _inputBuffer[i + area * 2] = ptr[src] * scale;      // B
            }
        }
        // 신뢰성 높은 순 정렬 -> IOU 필터 객체 다 지우기 == 신뢰도 높은 탐지 객체 중 IOU가 겹치지 않는 객체만 남음
        private List<(Rect, float, string)> ApplyNMS(List<(Rect box, float conf, string label)> detections)
        {
            var sorted = detections.OrderByDescending(d => d.conf).ToList();
            var keep = new List<(Rect, float, string)>();

            while (sorted.Count > 0)
            {
                var best = sorted[0];
                keep.Add(best);
                sorted.RemoveAt(0);
                sorted.RemoveAll(d => d.label == best.label && IoU(best.box, d.box) > IouThreshold);
            }

            return keep;
        }

        private float IoU(Rect a, Rect b)
        {
            int x1 = Math.Max(a.X, b.X);
            int y1 = Math.Max(a.Y, b.Y);
            int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            float intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            float union = a.Width * a.Height + b.Width * b.Height - intersection;

            return intersection / union;
        }

        public void Dispose()
        {
            _performanceLogger.Dispose();
            _session.Dispose();
        }
    }
}
