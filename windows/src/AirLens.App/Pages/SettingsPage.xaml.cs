using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirLens.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly AppController _app = App.Controller;
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
        AboutCard.Description = $"Version {version}. Phone-to-PC webcam: USB or Wi-Fi, no watermark, no kernel driver.";
        LogsCard.Description = AppController.LogFolder;
        Loaded += (_, _) =>
        {
            _loading = true;
            DecoderBox.SelectedItem = DecoderBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _app.Settings.Decoder) ?? DecoderBox.Items[0];
            _loading = false;
        };
    }

    private void OnDecoderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && DecoderBox.SelectedItem is ComboBoxItem { Tag: string tag }) _app.SetDecoder(tag);
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppController.LogFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppController.LogFolder}\"") { UseShellExecute = true });
    }
}
