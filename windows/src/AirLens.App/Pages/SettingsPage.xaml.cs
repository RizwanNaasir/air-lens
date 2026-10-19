using System.Reflection;
using AirLens.App.Updates;
using AirLens.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirLens.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly AppController _app = App.Controller;
    private readonly UpdateService _updates = App.Controller.Updates;
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
            AutoCheckToggle.IsOn = _app.Settings.CheckForUpdates;
            KeepInTrayToggle.IsOn = _app.Settings.KeepRunningInTray;
            _loading = false;
            _updates.Changed += RefreshUpdates;
            RefreshUpdates();
        };
        Unloaded += (_, _) => _updates.Changed -= RefreshUpdates;
    }

    private void OnDecoderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && DecoderBox.SelectedItem is ComboBoxItem { Tag: string tag }) _app.SetDecoder(tag);
    }

    private void OnKeepInTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.KeepRunningInTray = KeepInTrayToggle.IsOn;
        _app.Settings.Save();
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppController.LogFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppController.LogFolder}\"") { UseShellExecute = true });
    }

    // ---- updates -----------------------------------------------------------------------------

    private void RefreshUpdates()
    {
        var u = _updates;
        var latest = u.Latest;
        bool newer = latest is { Windows: not null } && UpdateVersion.IsNewer(latest.Version, u.Current);
        string next = latest is null ? "" : UpdateVersion.Display(latest.Version);

        UpdateCard.Header = $"AirLens {u.CurrentText}";
        UpdateCard.Description = u.Stage switch
        {
            UpdateStage.Checking => "Checking for updates…",
            UpdateStage.UpToDate => $"You're up to date. Checked {When(u.LastChecked)}.",
            UpdateStage.Available => $"Version {next} is available ({Megabytes(latest!.Windows!.Size)}). AirLens closes while it installs, then opens again.",
            UpdateStage.Downloading => $"Downloading {next}… {u.Progress:P0}",
            UpdateStage.Installing => $"Approve the Windows prompt to install {next}. AirLens closes and opens again when it's done.",
            UpdateStage.Failed => newer ? $"Version {next} is available, but the last try didn't work." : "The last check didn't work.",
            _ => u.LastChecked is null ? "Not checked yet." : $"Last checked {When(u.LastChecked)}.",
        };

        bool spinning = u.Stage is UpdateStage.Checking or UpdateStage.Installing;
        UpdateRing.IsActive = spinning;
        UpdateRing.Visibility = Show(spinning);
        UpdateProgress.Visibility = Show(u.Stage == UpdateStage.Downloading);
        UpdateProgress.Value = u.Progress;
        CancelButton.Visibility = Show(u.Stage == UpdateStage.Downloading);
        bool offerInstall = newer && u.Stage is UpdateStage.Available or UpdateStage.Failed;
        InstallButton.Visibility = Show(offerInstall);
        CheckButton.Visibility = Show(!offerInstall && !u.IsBusy);
        WhatsNewLink.Visibility = Show(newer && Uri.TryCreate(latest!.PageUrl, UriKind.Absolute, out _));
        if (newer && Uri.TryCreate(latest!.PageUrl, UriKind.Absolute, out var page)) WhatsNewLink.NavigateUri = page;

        UpdateError.IsOpen = u.Stage == UpdateStage.Failed && u.Error.Length > 0;
        UpdateError.Message = u.Error;
    }

    private void OnCheckUpdatesClick(object sender, RoutedEventArgs e) => _ = _updates.CheckAsync();

    private void OnCancelUpdateClick(object sender, RoutedEventArgs e) => _updates.CancelDownload();

    private bool _confirming; // WinUI allows one ContentDialog at a time; a second ShowAsync throws

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_confirming || _updates.Latest is not { } latest) return;
        _confirming = true;
        try
        {
            await ConfirmAndInstallAsync(latest);
        }
        finally
        {
            _confirming = false;
        }
    }

    private async Task ConfirmAndInstallAsync(ReleaseInfo latest)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Install AirLens {UpdateVersion.Display(latest.Version)}?",
            Content = "AirLens downloads the update, checks it against the release, and runs the installer. " +
                "Windows asks for permission once. AirLens closes while it installs and opens again afterwards. " +
                "The virtual camera stops for a few seconds, so finish any video call first.",
            PrimaryButtonText = "Update now",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await _updates.InstallAsync();
    }

    private void OnAutoCheckToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.CheckForUpdates = AutoCheckToggle.IsOn;
        _app.Settings.Save();
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static string Megabytes(long bytes) => $"{bytes / 1024.0 / 1024.0:0} MB";

    private static string When(DateTimeOffset? time)
    {
        if (time is not { } t) return "never";
        var local = t.ToLocalTime();
        return local.Date == DateTime.Today ? $"today at {local:HH:mm}" : $"on {local:d MMM} at {local:HH:mm}";
    }
}
