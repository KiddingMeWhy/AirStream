using System.Diagnostics;
using System.IO;
using System.Text.Json;

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
        await SendAsync(new { type = "start_capture" }, cancellationToken);
    }

    public async Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(new { type = "stop_capture" }, cancellationToken);
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
