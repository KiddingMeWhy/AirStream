
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AirStream.Core.Models;
using AirStream.Discovery;

namespace AirStream.App;

public partial class MainWindow : Window
{
    private bool _isConnected;

    private readonly AirPlayDiscoveryService _discovery;

    private readonly ObservableCollection<AirPlayDevice> _devices = new();

    public MainWindow()
    {
        InitializeComponent();

        _discovery = new AirPlayDiscoveryService();

        DeviceComboBox.ItemsSource = _devices;

        _discovery.DeviceFound += OnDeviceFound;
        _discovery.DeviceLost += OnDeviceLost;

        _discovery.Start();

        Closed += (_, _) => _discovery.Dispose();
    }

    private void OnDeviceFound(AirPlayDevice device)
    {
        Dispatcher.Invoke(() =>
        {
            if (_devices.Any(d => d.Id == device.Id))
                return;

            _devices.Add(device);

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

    private void UpdateDeviceSearchText()
    {
        DeviceSearchText.Text = _devices.Count == 0
            ? "Searching for AirPlay devices..."
            : $"{_devices.Count} AirPlay services found";
    }

    private void DeviceComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (DeviceComboBox.SelectedItem is AirPlayDevice device)
        {
            DeviceSearchText.Text = $"Selected: {device.Name}";
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
                new SolidColorBrush(Color.FromRgb(34, 197, 94));

            ConnectButton.Content = "Disconnect";
        }
        else
        {
            StatusText.Text = "Not connected";
            StatusIndicator.Fill =
                new SolidColorBrush(Color.FromRgb(100, 116, 139));

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
}
