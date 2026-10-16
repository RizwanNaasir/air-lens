using System.Runtime.InteropServices;

namespace AirLens.Core.VirtualCamera;

public enum VirtualCameraState
{
    Running,

    /// <summary>AirLens.VirtualCamera.dll is not installed next to the app.</summary>
    NotInstalled,

    /// <summary>Windows 10 or an edition without MFCreateVirtualCamera (needs build 22000+).</summary>
    NotSupported,

    /// <summary>Camera privacy settings deny desktop apps camera access.</summary>
    AccessDenied,
    Failed,
}

/// <summary>
/// Starts/stops the Media Foundation virtual camera through the exports in
/// AirLens.VirtualCamera.dll (Registration.cpp). Session lifetime: the camera disappears when
/// this process exits, even on a crash, so no stale "AirLens" camera is ever left behind.
/// </summary>
public static partial class VirtualCameraHost
{
    private const string Dll = "AirLens.VirtualCamera.dll";

    /// <summary>Where install.ps1 puts the DLL (Frame Server must be able to read it).</summary>
    public static readonly string InstalledPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AirLens", "VirtualCamera", Dll);

    static VirtualCameraHost()
    {
        // Load the same copy Frame Server loads (the registered one), falling back to the app folder.
        NativeLibrary.SetDllImportResolver(typeof(VirtualCameraHost).Assembly, (name, _, _) =>
            name == Dll && NativeLibrary.TryLoad(InstalledPath, out var handle) ? handle : IntPtr.Zero);
    }

    public static bool IsInstalled => File.Exists(InstalledPath);

    [LibraryImport(Dll, EntryPoint = "AirLensVCam_Start", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeStart(string friendlyName);

    [LibraryImport(Dll, EntryPoint = "AirLensVCam_Stop")]
    private static partial int NativeStop();

    public static (VirtualCameraState State, string Message) Start(string friendlyName = "AirLens")
    {
        try
        {
            int hr = NativeStart(friendlyName);
            return hr switch
            {
                >= 0 => (VirtualCameraState.Running, "virtual camera running"),
                unchecked((int)0x80070032) => (VirtualCameraState.NotSupported, "virtual cameras need Windows 11 (build 22000+)"),
                unchecked((int)0x80070005) => (VirtualCameraState.AccessDenied, "allow desktop apps to use the camera in Windows privacy settings"),
                _ => (VirtualCameraState.Failed, $"MFCreateVirtualCamera failed: 0x{hr:X8}"),
            };
        }
        catch (DllNotFoundException)
        {
            return (VirtualCameraState.NotInstalled, "virtual camera component not installed");
        }
        catch (EntryPointNotFoundException)
        {
            return (VirtualCameraState.NotInstalled, "virtual camera component is an incompatible version");
        }
    }

    public static void Stop()
    {
        try
        {
            NativeStop();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}
