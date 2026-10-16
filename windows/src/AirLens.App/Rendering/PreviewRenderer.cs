using System.Numerics;
using System.Runtime.InteropServices;
using AirLens.Core.Decoding;
using Vortice.D3DCompiler;
// FFmpeg.AutoGen also declares ID3D11* structs; import only what is needed from it.
using AVFrame = FFmpeg.AutoGen.AVFrame;
using AVPixelFormat = FFmpeg.AutoGen.AVPixelFormat;
using FF = FFmpeg.AutoGen.ffmpeg;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace AirLens.App.Rendering;

/// <summary>
/// GPU preview: decoder texture → (GPU copy) → NV12 shader-resource texture → NV12→RGB pixel
/// shader → flip-model composition swap chain → SwapChainPanel.
///
/// <b>Zero CPU copies for D3D11VA frames.</b> The swap chain lives on the decoder's own D3D11
/// device, so the decoded slice is copied texture-to-texture with <c>CopySubresourceRegion</c>.
/// Decoder texture arrays are usually bound only for decoding (no SRV), hence the one GPU copy.
/// The frame lease is released immediately after the copy, which keeps the decoder's surface pool free.
///
/// <b>Latency.</b> <c>FRAME_LATENCY_WAITABLE_OBJECT</c> with maximum latency 1: the render thread
/// waits until DXGI can take a frame, then takes the newest decoded frame, so display adds at most
/// one vsync. Frames that arrived in between were superseded in the mailbox, never queued.
///
/// Multithreading: the device has ID3D11Multithread protection on (see GpuSelector), which
/// serialises our immediate-context calls with FFmpeg's on the decoder thread.
/// </summary>
public sealed unsafe class PreviewRenderer : IDisposable
{
    private const string Shader = """
        cbuffer Params : register(b0) { float4 row0; float4 row1; float4 misc; };
        Texture2D<float>  lumaTex   : register(t0);
        Texture2D<float2> chromaTex : register(t1);
        SamplerState samp : register(s0);
        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);       // one triangle covering the screen
            o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            o.uv = uv;
            return o;
        }
        float4 PSMain(VSOut i) : SV_Target
        {
            float3 p = float3(i.uv, 1);
            float2 uv = float2(dot(row0.xyz, p), dot(row1.xyz, p));
            if (any(uv < 0) || any(uv > 1)) return float4(0, 0, 0, 1);  // letterbox
            float y = lumaTex.Sample(samp, uv);
            float2 c = chromaTex.Sample(samp, uv) - 0.5;
            if (misc.x < 0.5) { y = (y - 16.0 / 255.0) * (255.0 / 219.0); c *= 255.0 / 224.0; }  // limited range
            float3 rgb = float3(y + 1.5748 * c.y, y - 0.1873 * c.x - 0.4681 * c.y, y + 1.8556 * c.x);  // BT.709
            return float4(saturate(rgb), 1);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Vector4 Row0;
        public Vector4 Row1;
        public Vector4 Misc;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly GpuFrameMailbox _mailbox;
    private readonly Action<string> _log;
    private readonly IDXGISwapChain2 _swapChain;
    private readonly IntPtr _waitable;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _params;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private string? _lastError;
    private int _repeats;

    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _nv12;
    private ID3D11ShaderResourceView? _lumaSrv, _chromaSrv;
    private int _texWidth, _texHeight;
    private nint _cachedSourcePtr;
    private ID3D11Texture2D? _cachedSource;
    private AVFrame* _download = FF.av_frame_alloc();
    private byte[] _uploadScratch = [];
    private int _width, _height;
    private float _scaleX = 1f, _scaleY = 1f;
    private long _lastVersion;

    public PreviewRenderer(ID3D11Device device, GpuFrameMailbox mailbox, int width, int height, Action<string> log)
    {
        _device = device;
        _device.AddRef(); // the decoder owns the device too; we keep it alive for our own lifetime
        // Our own reference: Vortice's ImmediateContext property is one cached wrapper per device,
        // so disposing it here would kill it for the next renderer on the same device (a preview
        // that stays black after leaving Home and coming back).
        _context = new ID3D11DeviceContext(device.ImmediateContext.NativePointer);
        _context.AddRef(); // balanced by _context.Dispose()
        _mailbox = mailbox;
        _log = log;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };
        using var sc1 = factory.CreateSwapChainForComposition(device, desc, null);
        _swapChain = sc1.QueryInterface<IDXGISwapChain2>();
        _swapChain.MaximumFrameLatency = 1;
        _waitable = _swapChain.FrameLatencyWaitableObject;

        _vs = device.CreateVertexShader(Compiler.Compile(Shader, "VSMain", "preview.hlsl", "vs_5_0").Span);
        _ps = device.CreatePixelShader(Compiler.Compile(Shader, "PSMain", "preview.hlsl", "ps_5_0").Span);
        _sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
        _params = device.CreateBuffer((uint)sizeof(Params), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        CreateRenderTarget();

        _thread = new Thread(RenderLoop) { Name = "AirLens-Render", IsBackground = true, Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>The native swap chain pointer for ISwapChainPanelNative::SetSwapChain.</summary>
    public IntPtr SwapChainPointer => _swapChain.NativePointer;

    /// <summary>Clockwise rotation and mirroring the phone asked the receiver to apply (direct mode).</summary>
    public volatile int RotationDegrees;
    public volatile bool Mirror;
    public volatile bool FullRange;

    /// <summary>Virtual-camera framing, so the preview shows what apps get: centre zoom and fill.</summary>
    public volatile float Zoom = 1f;
    public volatile bool Fill;

    public void Start() => _thread.Start();

    /// <summary>Thread-safe; applied by the render thread before its next frame.</summary>
    public void Resize(int width, int height, float scaleX, float scaleY)
    {
        Volatile.Write(ref _pendingResizeBox, new ResizeRequest(Math.Max(1, width), Math.Max(1, height), scaleX, scaleY));
    }

    private sealed record ResizeRequest(int W, int H, float Sx, float Sy);
    private ResizeRequest? _pendingResizeBox;

    private void RenderLoop()
    {
        bool canPresent = false;
        while (_running)
        {
            try
            {
                if (!canPresent)
                {
                    WaitForSingleObjectEx(_waitable, 500, true);
                    canPresent = true;
                }
                ApplyResize();
                if (!_mailbox.WaitForNewFrame(_lastVersion, 100)) continue;
                if (!_mailbox.TryAcquire(ref _lastVersion, out var lease) || lease == null) continue;
                bool ok;
                using (lease) ok = Upload(lease);
                if (!ok) continue;
                Draw(lease.Meta.Width, lease.Meta.Height);
                _swapChain.Present(1, PresentFlags.None);
                canPresent = false;
            }
            catch (Exception e)
            {
                // One full report per distinct failure, then a count: a stuck error must not flood the log.
                string key = e.GetType().Name + e.Message;
                if (key != _lastError)
                {
                    _lastError = key;
                    _repeats = 0;
                    _log($"render: {e}");
                }
                else if (++_repeats % 100 == 0)
                {
                    _log($"render: same error {_repeats} more times");
                }
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Copies the frame into our NV12 texture. Returns false if the frame can't be shown.</summary>
    private bool Upload(FrameLease lease)
    {
        int w = lease.Meta.Width & ~1, h = lease.Meta.Height & ~1;
        if (w <= 0 || h <= 0) return false;
        EnsureTexture(w, h);
        // Read the frame once: every property of the lease re-reads it.
        AVFrame* frame = lease.Frame;
        if (frame == null) return false;
        if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            nint texture = (nint)frame->data[0];
            int slice = (int)(nint)frame->data[1];
            if (texture == 0) return false;
            if (texture != _cachedSourcePtr)
            {
                // The decoder uses one texture array for all frames; wrap it once. AddRef balances
                // the Release that Dispose of the wrapper performs.
                _cachedSource?.Dispose();
                _cachedSource = new ID3D11Texture2D(texture);
                _cachedSource.AddRef();
                _cachedSourcePtr = texture;
            }
            _context.CopySubresourceRegion(_nv12!, 0, 0, 0, 0, _cachedSource!, (uint)slice, new Box(0, 0, 0, w, h, 1));
            return true;
        }
        // CUDA or software frames: bring them to system-memory NV12 and upload (fallback path only).
        AVFrame* src = frame;
        if (src->hw_frames_ctx != null)
        {
            FF.av_frame_unref(_download);
            if (FF.av_hwframe_transfer_data(_download, src, 0) < 0) return false;
            src = _download;
        }
        if (src->format != (int)AVPixelFormat.AV_PIX_FMT_NV12) return false;
        int size = w * h * 3 / 2;
        if (_uploadScratch.Length < size) _uploadScratch = new byte[size];
        fixed (byte* dst = _uploadScratch)
        {
            for (int row = 0; row < h; row++) Buffer.MemoryCopy(src->data[0] + (long)row * src->linesize[0], dst + (long)row * w, w, w);
            for (int row = 0; row < h / 2; row++) Buffer.MemoryCopy(src->data[1] + (long)row * src->linesize[1], dst + (long)w * h + (long)row * w, w, w);
            _context.UpdateSubresource(_nv12!, 0, null, (IntPtr)dst, (uint)w, (uint)size);
        }
        return true;
    }

    private void EnsureTexture(int w, int h)
    {
        if (_nv12 != null && _texWidth == w && _texHeight == h) return;
        _lumaSrv?.Dispose();
        _chromaSrv?.Dispose();
        _nv12?.Dispose();
        _nv12 = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        _lumaSrv = _device.CreateShaderResourceView(_nv12, new ShaderResourceViewDescription(_nv12, ShaderResourceViewDimension.Texture2D, Format.R8_UNorm, 0, 1, 0, 1));
        _chromaSrv = _device.CreateShaderResourceView(_nv12, new ShaderResourceViewDescription(_nv12, ShaderResourceViewDimension.Texture2D, Format.R8G8_UNorm, 0, 1, 0, 1));
        _texWidth = w;
        _texHeight = h;
    }

    private void Draw(int videoW, int videoH)
    {
        var m = UvTransform(_width, _height, videoW, videoH, RotationDegrees, Mirror, Zoom, Fill);
        var p = new Params
        {
            Row0 = new Vector4(m.M11, m.M21, m.M31, 0),
            Row1 = new Vector4(m.M12, m.M22, m.M32, 0),
            Misc = new Vector4(FullRange ? 1 : 0, 0, 0, 0),
        };
        _context.UpdateSubresource(in p, _params, 0, 0, 0, null);
        if (_rtv == null) CreateRenderTarget(); // a failed resize left none: draw at the old size
        _context.OMSetRenderTargets(_rtv!, null);
        _context.RSSetViewport(0, 0, _width, _height);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.PSSetConstantBuffer(0, _params);
        _context.PSSetShaderResources(0, [_lumaSrv!, _chromaSrv!]);
        _context.PSSetSampler(0, _sampler);
        _context.Draw(3, 0);
        _context.PSSetShaderResources(0, [null!, null!]); // unbind before the next copy into _nv12
    }

    /// <summary>
    /// Screen UV (0..1, y down) → video UV: letterbox-fit the (rotated) video into the panel, then
    /// undo the receiver-side rotation and mirror. Row-vector convention: uv' = [u v 1] · M.
    /// </summary>
    public static Matrix3x2 UvTransform(int panelW, int panelH, int videoW, int videoH, int rotation, bool mirror, float zoom = 1f, bool fill = false)
    {
        bool quarter = rotation % 180 != 0;
        float panelAspect = (float)panelW / panelH;
        float videoAspect = quarter ? (float)videoH / videoW : (float)videoW / videoH;
        // Displayed video height in panel heights: fit (bars) or fill (crop), then the centre zoom.
        float s = fill ? MathF.Max(panelAspect / videoAspect, 1f) : MathF.Min(panelAspect / videoAspect, 1f);
        s *= MathF.Max(1f, zoom);
        float width = videoAspect * s; // displayed size in panel-height units
        float height = s;
        var toCentered = Matrix3x2.CreateTranslation(-0.5f, -0.5f);
        var fit = Matrix3x2.CreateScale(panelAspect / width, 1f / height);
        var flip = Matrix3x2.CreateScale(mirror ? -1f : 1f, 1f);
        // Inverse of a clockwise rotation in y-down coordinates.
        var unrotate = (((rotation % 360) + 360) % 360) switch
        {
            90 => new Matrix3x2(0, -1, 1, 0, 0, 0),
            180 => new Matrix3x2(-1, 0, 0, -1, 0, 0),
            270 => new Matrix3x2(0, 1, -1, 0, 0, 0),
            _ => Matrix3x2.Identity,
        };
        var back = Matrix3x2.CreateTranslation(0.5f, 0.5f);
        return toCentered * fit * flip * unrotate * back;
    }

    private void ApplyResize()
    {
        var r = Interlocked.Exchange(ref _pendingResizeBox, null);
        if (r == null) return;
        if (r.Sx != _scaleX || r.Sy != _scaleY)
        {
            // The panel shows the swap chain at DIP size; undo the composition (DPI) scale. Applied
            // even when the pixel size is unchanged, which is the case on the very first resize.
            _swapChain.MatrixTransform = Matrix3x2.CreateScale(1f / r.Sx, 1f / r.Sy);
            _scaleX = r.Sx;
            _scaleY = r.Sy;
        }
        if (r.W == _width && r.H == _height) return;
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
        _rtv?.Dispose();
        _rtv = null;
        _context.Flush();
        var result = _swapChain.ResizeBuffers(2, (uint)r.W, (uint)r.H, Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject);
        if (result.Success)
        {
            _width = r.W;
            _height = r.H;
        }
        else
        {
            _log($"render: resize to {r.W}x{r.H} failed (0x{result.Code:X8}); keeping {_width}x{_height}");
        }
        CreateRenderTarget();
    }

    private void CreateRenderTarget()
    {
        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(back, null);
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _context.ClearState();
        _lumaSrv?.Dispose();
        _chromaSrv?.Dispose();
        _nv12?.Dispose();
        _cachedSource?.Dispose();
        _rtv?.Dispose();
        _params.Dispose();
        _sampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Release();
        var download = _download;
        FF.av_frame_free(&download);
        _download = null;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, bool alertable);
}
