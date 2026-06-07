using System.Globalization;
using System.IO;
using System.Text;

namespace YoloWithWPF.Services
{
    internal sealed class InferenceCsvLogger : IDisposable
    {
        private const int FlushInterval = 10;

        private readonly object _lock = new();
        private readonly StreamWriter _writer;
        private int _pendingRows;
        private bool _disposed;

        public string FilePath { get; }

        public InferenceCsvLogger()
        {
            string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);

            FilePath = Path.Combine(
                logDirectory,
                $"inference_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            _writer = new StreamWriter(
                FilePath,
                append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            _writer.WriteLine(
                "Timestamp,PreprocessMs,InferenceMs,PostprocessMs,TotalMs");
            _writer.Flush();

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        public void Write(
            double preprocessMs,
            double inferenceMs,
            double postprocessMs,
            double totalMs)
        {
            string row = string.Join(",",
                DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture),
                preprocessMs.ToString("F3", CultureInfo.InvariantCulture),
                inferenceMs.ToString("F3", CultureInfo.InvariantCulture),
                postprocessMs.ToString("F3", CultureInfo.InvariantCulture),
                totalMs.ToString("F3", CultureInfo.InvariantCulture));

            lock (_lock)
            {
                if (_disposed)
                    return;

                _writer.WriteLine(row);

                if (++_pendingRows >= FlushInterval)
                {
                    _writer.Flush();
                    _pendingRows = 0;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                _writer.Dispose();
            }
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            Dispose();
        }
    }
}
