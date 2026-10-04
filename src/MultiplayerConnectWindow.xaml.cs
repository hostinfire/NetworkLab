using System.Net;
using System.Windows;

namespace SiscoNet;

public partial class MultiplayerConnectWindow : Window
{
    public string HostAddress => HostAddressBox.Text.Trim();
    public int Port { get; private set; }
    public string DisplayName => string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? Environment.UserName : DisplayNameBox.Text.Trim();

    public MultiplayerConnectWindow()
    {
        InitializeComponent();
        DisplayNameBox.Text = Environment.UserName;
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!IPAddress.TryParse(HostAddress, out _))
        {
            MessageBox.Show(this, "Enter a valid host IP address.", "Invalid host address", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Port must be a number from 1 to 65535.", "Invalid port", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Port = port;
        DialogResult = true;
    }
}