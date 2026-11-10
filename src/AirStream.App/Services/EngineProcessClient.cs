using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Buffers.Binary;
using System.IO.Pipes;

namespace AirStream.App.Services;

/// <summary>
/// Starts AirStream.Engine and communicates with it using newline-delimited JSON.
/// stdout is reserved for protocol messages; diagnostic output belongs on stderr.
/// </summary>
public sealed class EngineProcessClient : IAsyncDisposable
{
    private readonly string _executablePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;

    private NamedPipeServerStream? _audioPipe;
    private Task? _audioPipeTask;

    public event Action<byte[]>? AudioDataReceived;

    public event Action<JsonElement>? MessageReceived;
    public event Action<string>? DiagnosticReceived;

    public bool IsRunning => _process is { HasExited: false };

    public EngineProcessClient(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("An engine executable path is required.", nameof(executablePath));

        _executablePath = Path.GetFullPath(executablePath);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return;

        if (!File.Exists(_executablePath))
            throw new FileNotFoundException("AirStream.Engine executable was not found.", _executablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("AirStream.Engine could not be started.");
        }

        _process = process;
        _stdoutTask = ReadOutputAsync(process, cancellationToken);
        _stderrTask = ReadDiagnosticsAsync(process, cancellationToken);

        await Task.CompletedTask;
    }

    public async Task SendPingAsync(string requestId, CancellationToken cancellationToken = default)
    {
        await SendAsync(new { type = "ping", request_id = requestId }, cancellationToken);
    }

    public async Task StartCaptureAsync(CancellationToken cancellationToken = default)
    {
        if (_audioPipe is not null)
            throw new InvalidOperationException("The audio pipe is already active.");

        var pipeName = $"AirStreamAudio_{Guid.NewGuid():N}";

        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            65536,
            65536);

        _audioPipe = pipe;
        var connectionTask = pipe.WaitForConnectionAsync(cancellationToken);

        try
        {
            await SendAsync(
                new { type = "start_capture", pipe_name = pipeName },
                cancellationToken);

            await connectionTask.WaitAsync(
                TimeSpan.FromSeconds(15),
                cancellationToken);

            _audioPipeTask = ReadAudioPipeAsync(pipe, cancellationToken);
        }
        catch
        {
            await pipe.DisposeAsync();

            try
            {
                await connectionTask;
            }
            catch
            {
            }

            _audioPipe = null;
            throw;
        }
    }

    public async Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SendAsync(
                new { type = "stop_capture" },
                cancellationToken);

            if (_audioPipeTask is not null)
                await _audioPipeTask;
        }
        finally
        {
            if (_audioPipe is not null)
                await _audioPipe.DisposeAsync();

            _audioPipe = null;
            _audioPipeTask = null;
        }
    }

        private async Task ReadAudioPipeAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];

        while (!cancellationToken.IsCancellationRequested)
        {
            var headerBytes = await ReadExactlyAsync(
                pipe,
                lengthBuffer,
                cancellationToken);

            if (headerBytes == 0)
                break;

            if (headerBytes != lengthBuffer.Length)
                throw new InvalidDataException("Incomplete audio frame header.");

            var frameLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);

            if (frameLength <= 0 || frameLength > 1024 * 1024)
                throw new InvalidDataException($"Invalid audio frame length: {frameLength}.");

            var audioData = new byte[frameLength];

            var dataBytes = await ReadExactlyAsync(
                pipe,
                audioData,
                cancellationToken);

            if (dataBytes != frameLength)
                throw new InvalidDataException("Incomplete audio frame payload.");

            AudioDataReceived?.Invoke(audioData);
        }
    }

    private static async Task<int> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(
                buffer.AsMemory(totalRead),
                cancellationToken);

            if (bytesRead == 0)
                break;

            totalRead += bytesRead;
        }

        return totalRead;
    }

    private async Task SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited)
            throw new InvalidOperationException("AirStream.Engine is not running.");

        var json = JsonSerializer.Serialize(message);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadOutputAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    MessageReceived?.Invoke(document.RootElement.Clone());
                }
                catch (JsonException exception)
                {
                    DiagnosticReceived?.Invoke($"Invalid engine response: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during application shutdown.
        }
        catch (Exception exception)
        {
            DiagnosticReceived?.Invoke($"Engine output error: {exception.Message}");
        }
    }

    private async Task ReadDiagnosticsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;

                DiagnosticReceived?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during application shutdown.
        }
        catch (Exception exception)
        {
            DiagnosticReceived?.Invoke($"Engine diagnostic stream error: {exception.Message}");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null)
            return;

        if (_audioPipe is not null && !process.HasExited)
        {
            try
            {
                await StopCaptureAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                DiagnosticReceived?.Invoke(
                    $"Audio pipe shutdown error: {exception.Message}");
            }
        }

        if (!process.HasExited)
        {
            try
            {
                await SendAsync(new { type = "shutdown" }, cancellationToken);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // The process may have exited between the check and the write.
            }
        }

        if (_audioPipe is not null)
        {
            await _audioPipe.DisposeAsync();
        }

        _audioPipe = null;

        if (_audioPipeTask is not null)
        {
            try
            {
                await _audioPipeTask;
            }
            catch (Exception exception)
            {
                DiagnosticReceived?.Invoke(
                    $"Audio pipe reader stopped: {exception.Message}");
            }
        }

        _audioPipeTask = null;
        
        if (_stdoutTask is not null)
            await _stdoutTask;
        if (_stderrTask is not null)
            await _stderrTask;

        process.Dispose();
        _process = null;
        _stdoutTask = null;
        _stderrTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _writeLock.Dispose();
    }
}
