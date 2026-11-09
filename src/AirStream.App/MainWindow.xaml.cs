
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AirStream.Core.Models;
using AirStream.Discovery;
using AirStream.App.Services;
using MediaColor = System.Windows.Media.Color;

namespace AirStream.App;

public partial class MainWindow : Window
{
    private bool _isConnected;
    private bool _isExiting;
    private bool _savedDeviceRestored;

    private readonly AirPlayDiscoveryService _discovery;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;

    private readonly ObservableCollection<AirPlayDevice> _devices = new();
    private readonly SystemTrayService _trayService;

    public MainWindow()
    {
        InitializeComponent();

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
        Closed += (_, _) =>
        {
            _discovery.Dispose();
            _trayService.Dispose();
        };

        _discovery.Start();
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

            // Die gespeicherte ID bleibt erhalten,
            // damit die Auswahl beim nächsten Start
            // wiederhergestellt werden kann.

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

    private void ConnectButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _isConnected = !_isConnected;

        if (_isConnected)
        {
            StatusText.Text = "Connected (Demo)";
            StatusIndicator.Fill =
                new SolidColorBrush(MediaColor.FromRgb(34, 197, 94));

            ConnectButton.Content = "Disconnect";
        }
        else
        {
            StatusText.Text = "Not connected";
            StatusIndicator.Fill =
                new SolidColorBrush(MediaColor.FromRgb(100, 116, 139));

            ConnectButton.Content = "Connect";
        }
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
