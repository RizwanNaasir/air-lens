using AirLens.Core.Protocol;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AirLens.Core.Decoding;

public sealed record GpuAdapterInfo(
    string Description,
    uint VendorId,
    uint DeviceId,
    ulong DedicatedVideoMemory,
    Vortice.Luid Luid,
    bool SupportsH264,
    bool SupportsHevc)
{
    public bool IsNvidia => VendorId == 0x10DE;
    public bool IsAmd => VendorId == 0x1002;
    public bool IsIntel => VendorId == 0x8086;

    public bool Supports(VideoCodec codec) => codec == VideoCodec.Hevc ? SupportsHevc : SupportsH264;

    public override string ToString() => $"{Description} ({DedicatedVideoMemory / (1024 * 1024)} MB)";
}

/// <summary>
/// Picks the GPU to decode on and creates the D3D11 device that FFmpeg, the renderer and the
/// virtual-camera readback all share (one device = textures move between them without copies
/// across adapters).
///
/// Hybrid laptops (e.g. RTX 4060 + Radeon iGPU) are the reason this exists: the default adapter
/// is often the iGPU. NVIDIA is preferred whenever it advertises the codec's DXVA profile; on
/// NVIDIA drivers D3D11 video decoding runs on the same NVDEC engine that CUVID uses.
/// </summary>
public static class GpuSelector
{
    // DXVA decoder profile GUIDs (d3d11.h).
    private static readonly Guid H264VldNoFgt = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
    private static readonly Guid HevcVldMain = new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0");

    private static readonly FeatureLevel[] Levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

    public static IReadOnlyList<GpuAdapterInfo> Enumerate()
    {
        var result = new List<GpuAdapterInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
        {
            using (adapter)
            {
                var d = adapter!.Description1;
                if ((d.Flags & AdapterFlags.Software) != 0) continue;
                var (h264, hevc) = ProbeDecoders(adapter);
                result.Add(new GpuAdapterInfo(d.Description, d.VendorId, d.DeviceId, (ulong)d.DedicatedVideoMemory, d.Luid, h264, hevc));
            }
        }
        return result;
    }

    /// <summary>NVIDIA first, then the adapter with the most dedicated VRAM, among those that can decode <paramref name="codec"/>.</summary>
    public static GpuAdapterInfo? Choose(IReadOnlyList<GpuAdapterInfo> adapters, VideoCodec codec) =>
        adapters
            .Where(a => a.Supports(codec))
            .OrderByDescending(a => a.IsNvidia)
            .ThenByDescending(a => a.DedicatedVideoMemory)
            .FirstOrDefault();

    /// <summary>
    /// Creates a video-capable D3D11 device on <paramref name="adapter"/> with multithread
    /// protection on: FFmpeg decodes on one thread while the renderer copies on another.
    /// </summary>
    public static ID3D11Device CreateDevice(GpuAdapterInfo adapter)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? candidate).Success; i++)
        {
            using (candidate)
            {
                var luid = candidate!.Description1.Luid;
                if (luid.LowPart != adapter.Luid.LowPart || luid.HighPart != adapter.Luid.HighPart) continue;
                D3D11.D3D11CreateDevice(candidate, DriverType.Unknown,
                    DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport, Levels, out ID3D11Device? device).CheckError();
                using (var multithread = device!.ImmediateContext.QueryInterface<ID3D11Multithread>())
                {
                    multithread.SetMultithreadProtected(true);
                }
                return device;
            }
        }
        throw new InvalidOperationException($"adapter {adapter.Description} is no longer present");
    }

    private static (bool H264, bool Hevc) ProbeDecoders(IDXGIAdapter1 adapter)
    {
        if (D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.VideoSupport, Levels, out ID3D11Device? device).Failure
            || device == null)
        {
            return (false, false);
        }
        using (device)
        {
            using var video = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
            if (video == null) return (false, false);
            bool h264 = false, hevc = false;
            for (uint p = 0; p < video.VideoDecoderProfileCount; p++)
            {
                var guid = video.GetVideoDecoderProfile(p);
                h264 |= guid == H264VldNoFgt;
                hevc |= guid == HevcVldMain;
            }
            return (h264, hevc);
        }
    }
}
