using System.Text.Json;

namespace AirLens.App;

/// <summary>
/// What the app remembers between launches. Stored in %LOCALAPPDATA%\AirLens\settings.json: the
/// app is unpackaged, so the packaged-app settings store is not available, and the install folder
/// under Program Files is read-only for the user.
/// </summary>
public sealed class AppSettings
{
    /// <summary>The phone's Wi-Fi address. USB is always tried first; this is the fallback.</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// The phone last used (its discovery id). Discovery keeps <see cref="Host"/> pointing at this
    /// phone's current Wi-Fi address; other phones on the network are only offered, never joined.
    /// </summary>
    public string PhoneId { get; set; } = "";

    /// <summary>That phone's name ("HUAWEI P30"), for the status before it connects.</summary>
    public string PhoneName { get; set; } = "";

    /// <summary>Turn the virtual camera on at launch (it was on last time).</summary>
    public bool VirtualCamera { get; set; }

    /// <summary>Look for the phone as soon as AirLens starts.</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>Virtual camera framing (see VirtualCameraLook). "Hide video" is not kept on purpose.</summary>
    public bool CameraMirror { get; set; }
    public int CameraRotation { get; set; }
    public double CameraZoom { get; set; } = 1.0;
    public bool CameraFill { get; set; }

    /// <summary>"auto", "d3d11va", "cuda" or "software".</summary>
    public string Decoder { get; set; } = "auto";

    /// <summary>Look for a new version about once a day, shortly after AirLens starts. Installing always asks.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the update check last reached the release feed.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirLens", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
