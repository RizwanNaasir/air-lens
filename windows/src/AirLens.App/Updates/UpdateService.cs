using System.Diagnostics;
using System.Reflection;
using AirLens.Core.Updates;
using Microsoft.UI.Dispatching;

namespace AirLens.App.Updates;

public enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Installing,
    Failed,
}

/// <summary>
/// Keeps AirLens current from the public GitHub releases. A check runs about once a day after
/// start-up (if allowed) or on demand; installing is always the user's click. Install = download the
/// setup zip, check its SHA-256 against the release, unpack it, and run its Setup.cmd. Windows then
/// asks for administrator rights once, the installer closes AirLens, updates it and starts it again.
/// Lives on the UI thread; <see cref="Changed"/> fires there.
/// </summary>
public sealed class UpdateService
{
    private static readonly TimeSpan AutoCheckEvery = TimeSpan.FromHours(20);
    private static readonly TimeSpan AutoCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private readonly DispatcherQueue _ui;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _work;

    public UpdateService(DispatcherQueue ui, AppSettings settings)
    {
        _ui = ui;
        _settings = settings;
        string informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        Current = UpdateVersion.Parse(informational.Split('+')[0]) ?? new Version(0, 0, 0, 0);
    }

    /// <summary>Where releases are looked up: GitHub, or <c>--update-feed &lt;url&gt;</c> for testing.</summary>
    public Uri Feed { get; set; } = UpdateFeed.GitHubLatest;

    public Version Current { get; }
    public string CurrentText => UpdateVersion.Display(Current);
    public UpdateStage Stage { get; private set; } = UpdateStage.Idle;
    public ReleaseInfo? Latest { get; private set; }
    public double Progress { get; private set; }
    public string Error { get; private set; } = "";
    public DateTimeOffset? LastChecked => _settings.LastUpdateCheck;
    public bool IsBusy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Installing;

    public event Action? Changed;

    private static string UpdatesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirLens", "updates");

    /// <summary>
    /// The once-a-day check, a little after start-up so it never slows the first connect. Also
    /// removes the files of an earlier update (~430 MB): by now its installer has run, or was declined.
    /// </summary>
    public void StartAutoCheck()
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(UpdatesFolder)) Directory.Delete(UpdatesFolder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppController.Log($"update: could not remove old update files: {e.Message}");
            }
        });
        var timer = _ui.CreateTimer();
        timer.Interval = AutoCheckDelay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            bool due = _settings.LastUpdateCheck is not { } last || DateTimeOffset.UtcNow - last > AutoCheckEvery;
            if (_settings.CheckForUpdates && due && !IsBusy) _ = CheckAsync(automatic: true);
        };
        timer.Start();
    }

    public async Task CheckAsync(bool automatic = false)
    {
        if (IsBusy) return;
        Set(UpdateStage.Checking);
        try
        {
            var latest = await UpdateFeed.FetchLatestAsync(Http, Feed);
            _settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            _settings.Save();
            Latest = latest;
            bool newer = latest is { Windows: not null } && UpdateVersion.IsNewer(latest.Version, Current);
            AppController.Log(newer
                ? $"update: {UpdateVersion.Display(latest!.Version)} is available ({(automatic ? "daily check" : "checked by hand")})"
                : $"update: up to date at {CurrentText}");
            Set(newer ? UpdateStage.Available : UpdateStage.UpToDate);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            AppController.Log($"update check failed: {e.Message}");
            if (automatic) Set(UpdateStage.Idle); // a failed background check stays quiet
            else Fail($"Couldn't reach the update server: {e.Message}");
        }
    }

    /// <summary>Downloads, verifies and starts the installer for <see cref="Latest"/>.</summary>
    public async Task InstallAsync()
    {
        if (IsBusy || Latest is not { Windows: { } asset } release) return;
        _work = new CancellationTokenSource();
        string updates = UpdatesFolder;
        try
        {
            Progress = 0;
            Set(UpdateStage.Downloading);
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                Changed?.Invoke();
            });
            string zip = await UpdateFeed.DownloadAsync(Http, asset, updates, progress, _work.Token);
            AppController.Log($"update: downloaded and verified {asset.Name}");

            Set(UpdateStage.Installing);
            string setup = await Task.Run(() => UpdateFeed.ExtractSetup(zip, Path.Combine(updates, Path.GetFileNameWithoutExtension(zip))));
            var installer = Process.Start(new ProcessStartInfo(setup)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(setup)!,
            }) ?? throw new IOException("Windows did not start the installer");
            AppController.Log($"update: started the installer for {UpdateVersion.Display(release.Version)}");
            _ = WatchInstallerAsync(installer);
        }
        catch (OperationCanceledException)
        {
            Set(UpdateStage.Available);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppController.Log($"update failed: {e.Message}");
            Fail($"The update didn't install: {e.Message}");
        }
    }

    public void CancelDownload() => _work?.Cancel();

    /// <summary>
    /// On success the installer closes AirLens before it finishes, so we only see it end when it
    /// did not install: the Windows prompt was declined, or setup failed.
    /// </summary>
    private async Task WatchInstallerAsync(Process installer)
    {
        await installer.WaitForExitAsync();
        int code = installer.ExitCode;
        _ui.TryEnqueue(() =>
        {
            AppController.Log($"update: installer ended with exit code {code}");
            if (code != 0) Fail("The update was not installed. Windows' permission prompt may have been declined. Details are in C:\\ProgramData\\AirLens\\install.log.");
            else Set(UpdateStage.Available);
        });
    }

    private void Fail(string message)
    {
        Error = message;
        Set(UpdateStage.Failed);
    }

    private void Set(UpdateStage stage)
    {
        Stage = stage;
        if (stage != UpdateStage.Failed) Error = "";
        Changed?.Invoke();
    }
}
