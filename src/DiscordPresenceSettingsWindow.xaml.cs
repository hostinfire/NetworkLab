using System.Diagnostics;
using System.Windows;

namespace SiscoNet;

public partial class DiscordPresenceSettingsWindow : Window
{
    public string ApplicationId { get; private set; } = "";

    public DiscordPresenceSettingsWindow(string applicationId)
    {
        InitializeComponent();
        ApplicationIdBox.Text = applicationId;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplicationId = ApplicationIdBox.Text.Trim();
        if (ApplicationId.Length is < 17 or > 20 || !ulong.TryParse(ApplicationId, out _))
        {
            MessageBox.Show(this, "Enter the 17- to 20-digit numeric Application ID shown in your Discord Developer Portal application settings.",
                "Invalid Application ID", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        ApplicationId = "";
        DialogResult = true;
    }

    private void DeveloperPortal_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open the Developer Portal", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}