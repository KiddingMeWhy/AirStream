using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace AirStream.App;

public partial class MainWindow : Window
{
    private bool _isConnected = false;

    public MainWindow()
    {
        InitializeComponent();
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
                new SolidColorBrush(
                    Color.FromRgb(34, 197, 94));

            ConnectButton.Content = "Disconnect";
        }
        else
        {
            StatusText.Text = "Not connected";
            StatusIndicator.Fill =
                new SolidColorBrush(
                    Color.FromRgb(100, 116, 139));

            ConnectButton.Content = "Connect to HomePod";
        }
    }

    private void VolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText != null)
        {
            VolumeText.Text =
                $"{(int)VolumeSlider.Value}%";
        }
    }
}
