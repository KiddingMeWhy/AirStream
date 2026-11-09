
using System.Runtime.Versioning;
using NAudio.Wave;

namespace AirStream.Audio;

[SupportedOSPlatform("windows")]
public sealed class WasapiLoopbackCaptureService : IDisposable
{
    private readonly object _sync = new();

    private WasapiRecorder? _recorder;
    private bool _disposed;

    public bool IsCapturing
    {
        get
        {
            lock (_sync)
            {
                return _recorder != null;
            }
        }
    }

    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    public event Action<Exception>? CaptureError;

    public event Action? CaptureStopped;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_recorder != null)
                return;

            var recorder = new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .Build();

            recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
            {
                var data = buffer.ToArray();
                var format = recorder.WaveFormat;

                DataAvailable?.Invoke(
                    this,
                    new AudioDataEventArgs(
                        data,
                        data.Length,
                        format.SampleRate,
                        format.Channels,
                        format.BitsPerSample));
            };

            recorder.RecordingStopped += OnRecordingStopped;

            _recorder = recorder;

            try
            {
                recorder.StartRecording();
            }
            catch
            {
                _recorder = null;
                recorder.RecordingStopped -= OnRecordingStopped;
                recorder.Dispose();
                throw;
            }
        }
    }

    public void Stop()
    {
        WasapiRecorder? recorder;

        lock (_sync)
        {
            recorder = _recorder;
        }

        recorder?.StopRecording();
    }

    private void OnRecordingStopped(
        object? sender,
        StoppedEventArgs e)
    {
        WasapiRecorder? recorder;

        lock (_sync)
        {
            recorder = _recorder;

            if (recorder == null)
                return;

            _recorder = null;
        }

        recorder.RecordingStopped -= OnRecordingStopped;
        recorder.Dispose();

        CaptureStopped?.Invoke();

        if (e.Exception != null)
            CaptureError?.Invoke(e.Exception);
    }

    public void Dispose()
    {
        WasapiRecorder? recorder;

        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            recorder = _recorder;
        }

        recorder?.StopRecording();
    }
}
