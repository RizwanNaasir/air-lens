using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace AirLens.Core.VirtualCamera;

/// <summary>
/// The virtual camera's GPU step: decoded D3D11 frame → D3D11 video processor (crop for zoom and
/// fill, rotate, mirror, scale, black bars) → NV12 at exactly the size the camera app asked for →
/// one small staging copy → shared memory.
///
/// Before, the full 4K frame was copied to system memory and Frame Server scaled it on the CPU.
/// Now the GPU scales and only the output size crosses the bus (a quarter of the bytes for 1080p),
/// and mirror, rotation and zoom come free with the scaling.
///
/// Runs on the virtual-camera thread. The decoder's device is shared with FFmpeg and the preview,
/// so the immediate context is multithread-protected (FFmpeg enables it; this class makes sure).
/// </summary>
public sealed unsafe class VirtualCameraGpuStage : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _video;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoContext1? _videoContext1;
    private readonly Dictionary<(nint Texture, int Slice), ID3D11VideoProcessorInputView> _inputViews = new();

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private (int SrcW, int SrcH, int DstW, int DstH) _size;
    private ID3D11Texture2D? _output;
    private ID3D11Texture2D? _staging;
    private ID3D11VideoProcessorOutputView? _outputView;
    private nint _viewsTexture;

    public VirtualCameraGpuStage(nint devicePointer)
    {
        _device = new ID3D11Device(devicePointer);
        _device.AddRef();
        _context = new ID3D11DeviceContext(_device.ImmediateContext.NativePointer);
        _context.AddRef();
        using (var mt = _context.QueryInterfaceOrNull<ID3D11Multithread>()) mt?.SetMultithreadProtected(true);
        _video = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();
        _videoContext1 = _context.QueryInterfaceOrNull<ID3D11VideoContext1>();
    }

    public nint DevicePointer => _device.NativePointer;

    /// <summary>False when the GPU cannot mirror (no ID3D11VideoContext1); everything else still works.</summary>
    public bool CanMirror => _videoContext1 != null;

    /// <summary>
    /// Queues the processing of one decoded frame (<paramref name="texture"/> slice
    /// <paramref name="slice"/>) into the staging copy. Call while holding the frame's lease; the
    /// GPU reads the decoder's surface in queue order, so the lease can end right after this.
    /// </summary>
    public void Submit(nint texture, int slice, int srcW, int srcH, int dstW, int dstH,
                       int rotationDegrees, bool mirror, VirtualCameraLook look, bool fullRange)
    {
        EnsureProcessor(srcW, srcH, dstW, dstH);
        var input = InputView(texture, slice);
        var (src, dst) = VirtualCameraGeometry.Compute(srcW, srcH, dstW, dstH, rotationDegrees, look.Zoom, look.Fill);
        var proc = _processor!;

        var colorSpace = new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0, Nominal_Range = fullRange ? 2u : 1u };
        _videoContext.VideoProcessorSetStreamColorSpace(proc, 0, colorSpace);
        _videoContext.VideoProcessorSetOutputColorSpace(proc, colorSpace);
        _videoContext.VideoProcessorSetStreamFrameFormat(proc, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamSourceRect(proc, 0, true, new RawRect(src.Left, src.Top, src.Right, src.Bottom));
        _videoContext.VideoProcessorSetStreamDestRect(proc, 0, true, new RawRect(dst.Left, dst.Top, dst.Right, dst.Bottom));
        _videoContext.VideoProcessorSetOutputTargetRect(proc, true, new RawRect(0, 0, dstW, dstH));
        // Black bars in limited-range YCbCr.
        _videoContext.VideoProcessorSetOutputBackgroundColor(proc, true, new VideoColor { YCbCr = new VideoColorYCbCrA { Y = 16f / 255, Cb = 0.5f, Cr = 0.5f, A = 1f } });
        _videoContext.VideoProcessorSetStreamRotation(proc, 0, true, VirtualCameraGeometry.Normalize(rotationDegrees) switch
        {
            90 => VideoProcessorRotation.Rotation90,
            180 => VideoProcessorRotation.Rotation180,
            270 => VideoProcessorRotation.Rotation270,
            _ => VideoProcessorRotation.Identity,
        });
        // The processor mirrors the input before rotating it. "Mirror" means left-right as seen,
        // which for a quarter turn is an up-down flip of the input.
        bool quarter = VirtualCameraGeometry.Normalize(rotationDegrees) % 180 != 0;
        _videoContext1?.VideoProcessorSetStreamMirror(proc, 0, mirror, mirror && !quarter, mirror && quarter);

        var stream = new VideoProcessorStream { Enable = true, InputSurface = input };
        _videoContext.VideoProcessorBlt(proc, _outputView!, 0, 1, [stream]);
        _context.CopyResource(_staging!, _output!);
    }

    /// <summary>Maps the staging copy (waits for the GPU) and publishes it. Call after the lease ended.</summary>
    public bool Publish(SharedMemoryFrameWriter writer)
    {
        if (_staging == null) return false;
        var (_, _, w, h) = _size;
        var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* y = (byte*)mapped.DataPointer;
            // NV12 staging textures keep the chroma plane right after the luma rows.
            byte* uv = y + (long)mapped.RowPitch * h;
            return writer.WriteNv12(y, (int)mapped.RowPitch, uv, (int)mapped.RowPitch, w, h);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    private void EnsureProcessor(int srcW, int srcH, int dstW, int dstH)
    {
        if (_processor != null && _size == (srcW, srcH, dstW, dstH)) return;
        ReleaseProcessor();
        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(30, 1),
            InputWidth = (uint)srcW,
            InputHeight = (uint)srcH,
            OutputFrameRate = new Rational(30, 1),
            OutputWidth = (uint)dstW,
            OutputHeight = (uint)dstH,
            Usage = VideoUsage.OptimalQuality,
        };
        _video.CreateVideoProcessorEnumerator(ref desc, out _enumerator).CheckError();
        _video.CreateVideoProcessor(_enumerator!, 0, out _processor).CheckError();
        _output = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)dstW,
            Height = (uint)dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)dstW,
            Height = (uint)dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        var outDesc = new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D };
        _video.CreateVideoProcessorOutputView(_output, _enumerator!, outDesc, out _outputView).CheckError();
        _size = (srcW, srcH, dstW, dstH);
    }

    private ID3D11VideoProcessorInputView InputView(nint texture, int slice)
    {
        if (texture != _viewsTexture)
        {
            // The decoder allocated a new surface pool (new stream or reopen): old views are stale.
            foreach (var v in _inputViews.Values) v.Dispose();
            _inputViews.Clear();
            _viewsTexture = texture;
        }
        if (_inputViews.TryGetValue((texture, slice), out var view)) return view;
        using var resource = new ID3D11Texture2D(texture);
        resource.AddRef(); // balanced by the using: the decoder keeps its own reference
        var desc = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = (uint)slice },
        };
        _video.CreateVideoProcessorInputView(resource, _enumerator!, desc, out view).CheckError();
        _inputViews[(texture, slice)] = view!;
        return view!;
    }

    private void ReleaseProcessor()
    {
        foreach (var v in _inputViews.Values) v.Dispose();
        _inputViews.Clear();
        _viewsTexture = 0;
        _outputView?.Dispose();
        _outputView = null;
        _output?.Dispose();
        _output = null;
        _staging?.Dispose();
        _staging = null;
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    public void Dispose()
    {
        ReleaseProcessor();
        _videoContext1?.Dispose();
        _videoContext.Dispose();
        _video.Dispose();
        _context.Dispose();
        _device.Release();
    }
}
