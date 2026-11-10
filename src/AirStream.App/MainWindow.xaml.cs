
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AirStream.Core.Models;
using AirStream.Discovery;
using AirStream.App.Services;
using AirStream.Audio;
using MediaColor = System.Windows.Media.Color;
using System.IO;

namespace AirStream.App;

public partial class MainWindow : Window
{
    private bool _isExiting;
    private bool _savedDeviceRestored;

    private readonly AirPlayDiscoveryService _discovery;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly SystemTrayService _trayService;
    private readonly WasapiLoopbackCaptureService _audioCapture;
    private readonly EngineProcessClient _engine;

    private readonly ObservableCollection<AirPlayDevice> _devices = new();

    private long _capturedBytes;
    private int _audioDataReceived;
    private bool _engineCaptureActive;

    public MainWindow()
    {
        InitializeComponent();

        _engine = new EngineProcessClient(
            Path.Combine(AppContext.BaseDirectory, "AirStream.Engine.exe"));

        _engine.MessageReceived += OnEngineMessageReceived;
        _engine.AudioDataReceived += OnEngineAudioDataReceived;
        _engine.DiagnosticReceived += diagnostic =>
            Debug.WriteLine($"[AirStream.Engine] {diagnostic}");

        _ = StartEngineAsync();

        _audioCapture = new WasapiLoopbackCaptureService();

        _audioCapture.DataAvailable += OnAudioDataAvailable;
        _audioCapture.CaptureError += OnAudioCaptureError;
        _audioCapture.CaptureStopped += OnAudioCaptureStopped;

        _settingsService = new SettingsService();
        _settings = _settingsService.Load();

        _discovery = new AirPlayDiscoveryService();

        DeviceComboBox.ItemsSource = _devices;

        _discovery.DeviceFound += OnDeviceFound;
        _discovery.DeviceUpdated += OnDeviceUpdated;
        _discovery.DeviceLost += OnDeviceLost;

        _trayService = new SystemTrayService();

        _trayService.OpenRequested += RestoreWindow;
        _trayService.ExitRequested += ExitApplication;

        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;

        Closed += async (_, _) =>
        {
            await _engine.DisposeAsync();
            _audioCapture.Dispose();
            _discovery.Dispose();
            _trayService.Dispose();
        };

        _discovery.Start();
    }

    private async Task StartEngineAsync()
    {
        try
        {
            await _engine.StartAsync();
            await _engine.SendPingAsync(Guid.NewGuid().ToString("N"));
        }
        catch (Exception ex)
        {
            // The existing UI and audio-capture workflow remain usable if the native
            // engine is unavailable during this initial integration stage.
            Debug.WriteLine($"Could not start AirStream.Engine: {ex.Message}");
        }
    }

    private void OnDeviceFound(AirPlayDevice device)
    {
        Dispatcher.Invoke(() =>
        {
            if (_devices.Any(d => d.Id == device.Id))
                return;

            _devices.Add(device);

            TryRestoreSavedDeviceSelection();
            UpdateDeviceSearchText();
        });
    }

    private void OnDeviceUpdated(AirPlayDevice device)
    {
        Dispatcher.Invoke(() =>
        {
            var index = _devices
                .ToList()
                .FindIndex(d => d.Id == device.Id);

            if (index >= 0)
            {
                var wasSelected =
                    DeviceComboBox.SelectedItem is AirPlayDevice selected &&
                    selected.Id == device.Id;

                _devices[index] = device;

                if (wasSelected)
                    DeviceComboBox.SelectedItem = device;
            }
            else
            {
                _devices.Add(device);
            }

            TryRestoreSavedDeviceSelection();
            UpdateDeviceSearchText();
        });
    }

    private void OnDeviceLost(string deviceId)
    {
        Dispatcher.Invoke(() =>
        {
            var device = _devices.FirstOrDefault(
                d => d.Id == deviceId);

            if (device != null)
                _devices.Remove(device);

            UpdateDeviceSearchText();
        });
    }

    private void TryRestoreSavedDeviceSelection()
    {
        if (_savedDeviceRestored ||
            string.IsNullOrWhiteSpace(_settings.LastSelectedDeviceId))
        {
            return;
        }

        var savedDevice = _devices.FirstOrDefault(
            d => d.Id == _settings.LastSelectedDeviceId);

        if (savedDevice == null)
            return;

        DeviceComboBox.SelectedItem = savedDevice;
        _savedDeviceRestored = true;
    }

    private void UpdateDeviceSearchText()
    {
        DeviceSearchText.Text = _devices.Count == 0
            ? "Searching for AirPlay devices..."
            : $"{_devices.Count} speaker(s) found";
    }

    private void DeviceComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (DeviceComboBox.SelectedItem is AirPlayDevice device)
        {
            DeviceSearchText.Text = $"Selected: {device.Name}";

            _settings.LastSelectedDeviceId = device.Id;
            _settingsService.Save(_settings);
        }
    }

    private async void ConnectButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            if (_engineCaptureActive)
            {
                await _engine.StopCaptureAsync();
                _engineCaptureActive = false;
                return;
            }

            if (!_engine.IsRunning)
                await _engine.StartAsync();

            Interlocked.Exchange(ref _capturedBytes, 0);
            Interlocked.Exchange(ref _audioDataReceived, 0);

            await _engine.StartCaptureAsync();

            _engineCaptureActive = true;

            StatusText.Text = "Starting Rust audio capture...";

            StatusIndicator.Fill =
                new SolidColorBrush(
                    MediaColor.FromRgb(34, 197, 94));

            ConnectButton.Content = "Stop Capture";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Rust audio capture unavailable";

            StatusIndicator.Fill =
                new SolidColorBrush(
                    MediaColor.FromRgb(239, 68, 68));

            ConnectButton.Content = "Start Capture";

            System.Windows.MessageBox.Show(
                ex.Message,
                "AirStream - Engine Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnEngineAudioDataReceived(byte[] audioData)
    {
        Interlocked.Add(ref _capturedBytes, audioData.Length);

        if (Interlocked.Exchange(ref _audioDataReceived, 1) == 0)
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = "PCM audio received through named pipe";
            });
        }
    }

    private void OnEngineMessageReceived(System.Text.Json.JsonElement message)
    {
        if (!message.TryGetProperty("type", out var typeElement))
            return;

        var type = typeElement.GetString();

        Dispatcher.BeginInvoke(() =>
        {
            switch (type)
            {
                case "capture_started":
                {
                    var deviceName = message.TryGetProperty("device", out var deviceElement)
                        ? deviceElement.GetString()
                        : "Default audio device";
                    var sampleRate = message.TryGetProperty("sample_rate", out var rateElement)
                        ? rateElement.GetInt32()
                        : 0;
                    StatusText.Text = $"Rust capture active - {deviceName} ({sampleRate} Hz)";
                    break;
                }

                case "capture_stats":
                {
                    var receivedBytes = Interlocked.Read(ref _capturedBytes);

                    StatusText.Text = receivedBytes > 0
                        ? $"PCM received: {receivedBytes:N0} bytes"
                        : "WASAPI active - waiting for PCM audio";

                    break;
                }

                case "capture_stopped":
                    _engineCaptureActive = false;
                    StatusText.Text = "Audio capture stopped";
                    StatusIndicator.Fill =
                        new SolidColorBrush(
                            MediaColor.FromRgb(100, 116, 139));
                    ConnectButton.Content = "Start Capture";
                    break;

                case "error":
                {
                    _engineCaptureActive = false;
                    var errorMessage = message.TryGetProperty("message", out var errorElement)
                        ? errorElement.GetString()
                        : "Unknown engine error";
                    StatusText.Text = "Audio engine error";
                    StatusIndicator.Fill =
                        new SolidColorBrush(
                            MediaColor.FromRgb(239, 68, 68));
                    ConnectButton.Content = "Start Capture";
                    Debug.WriteLine($"[AirStream.Engine] {errorMessage}");
                    break;
                }
            }
        });
    }

    private void VolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText != null)
        {
            VolumeText.Text = $"{(int)VolumeSlider.Value}%";
        }
    }

    private void OnAudioDataAvailable(
        object? sender,
        AudioDataEventArgs e)
    {
        Interlocked.Add(
            ref _capturedBytes,
            e.BytesRecorded);

        if (Interlocked.Exchange(
            ref _audioDataReceived, 1) == 0)
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text =
                    "Capturing audio - Signal detected";
            });
        }
    }

    private void OnAudioCaptureError(Exception exception)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = "Audio capture error";

            StatusIndicator.Fill =
                new SolidColorBrush(
                    MediaColor.FromRgb(239, 68, 68));

            ConnectButton.Content = "Start Capture";

            System.Windows.MessageBox.Show(
                exception.Message,
                "AirStream - Audio Capture Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        });
    }

    private void OnAudioCaptureStopped()
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = "Audio capture stopped";

            StatusIndicator.Fill =
                new SolidColorBrush(
                    MediaColor.FromRgb(100, 116, 139));

            ConnectButton.Content = "Start Capture";
        });
    }

    private void MainWindow_StateChanged(
        object? sender,
        EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    private void MainWindow_Closing(
        object? sender,
        System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void RestoreWindow()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitApplication()
    {
        Dispatcher.Invoke(() =>
        {
            _isExiting = true;
            _trayService.Dispose();
            Close();
        });
    }
}
