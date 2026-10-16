using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using AirLens.Core.Buffering;
using AirLens.Core.Protocol;

namespace AirLens.Core.Transport;

public enum EndpointKind
{
    Usb,
    Wifi,
}

public sealed record StreamEndpoint(EndpointKind Kind, string Host, int Port)
{
    public static StreamEndpoint Usb(int port = ProtocolConstants.DefaultPort) => new(EndpointKind.Usb, "127.0.0.1", port);
    public static StreamEndpoint Wifi(string host, int port = ProtocolConstants.DefaultPort) => new(EndpointKind.Wifi, host, port);
    public override string ToString() => Kind == EndpointKind.Usb ? $"USB (localhost:{Port})" : $"Wi-Fi {Host}:{Port}";
}

/// <summary>Callbacks run on the receive thread (OnStatus also from the USB check); keep them short and never block.</summary>
public interface IStreamClientListener
{
    void OnConnected(StreamEndpoint endpoint);
    void OnDisconnected(StreamEndpoint endpoint, string reason);
    void OnStreamInfo(StreamInfo info);

    /// <summary>Takes ownership of <paramref name="frame"/>.</summary>
    void OnFrame(PacketBuffer frame);

    void OnRoundTrip(double rttMs);
    void OnStatus(string message);

    /// <summary>The phone's lenses and quality presets (DEVICE_STATE).</summary>
    void OnDeviceState(DeviceState state) { }
}

/// <summary>
/// The receive loop: connect → HELLO → read packets, forever, on one dedicated thread.
///
/// Resilience is plain control flow, not exception handling at the edges: any socket error, EOF,
/// timeout or protocol violation closes the socket, reports why, waits with backoff
/// (250 ms → 2 s, reset after a healthy session) and tries the endpoints again in order
/// (USB first). Nothing escapes the thread.
///
/// Back to USB: while a session runs on Wi-Fi and USB comes first in the list, a timer asks the
/// ADB server every few seconds whether a phone is attached (a socket query, no adb process).
/// When one is, it opens the USB link next to the running session and does the whole handshake,
/// up to the first packet. Only then does the Wi-Fi session end, and the receive thread carries on
/// over the open USB link, so the picture pauses for about a keyframe. The phone switches to the
/// new connection as soon as the HELLO arrives. If USB does not answer, Wi-Fi is never touched and
/// the check rests for 30 s.
///
/// Liveness: a <see cref="Timer"/> sends PING every 500 ms (the phone drops silent clients after
/// 3 s) and the socket read timeout is 3 s (the phone sends at least a HEARTBEAT every second).
///
/// Payloads are read straight into pooled buffers: no per-frame allocation, no intermediate copy.
/// </summary>
public sealed class StreamClient : IDisposable
{
    private const int ReadTimeoutMs = 3000;
    private const int ConnectTimeoutMs = 1500;
    private const int PingIntervalMs = 500;
    private static readonly TimeSpan MinKeyframeInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan UsbQuietAfterFailure = TimeSpan.FromSeconds(30);

    private volatile IReadOnlyList<StreamEndpoint> _endpoints;
    private readonly PacketBufferPool _pool;
    private readonly IStreamClientListener _listener;
    private readonly IUsbLink _usb;
    private readonly TimeSpan _usbCheckInterval;
    private readonly object _sendLock = new();
    private readonly byte[] _sendBuffer = new byte[64];
    private readonly byte[] _header = new byte[ProtocolConstants.HeaderSize];
    private readonly byte[] _scratch = new byte[4096];
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _thread;
    private Socket? _socket;
    private int _sessionId; // changes with every connected session, under _sendLock
    private Timer? _pingTimer;
    private Timer? _usbTimer;
    private readonly TimerCallback _pingCallback;
    private long _lastKeyframeRequestTicks;
    private uint _currentEpoch;
    private int _usbChecking;
    private long _usbQuietUntilTicks;
    private UsbProbe? _probe;

    /// <summary>A USB link opened next to a Wi-Fi session: handshake done, first header already read.</summary>
    private sealed record Handoff(StreamEndpoint Endpoint, Socket Socket, byte[] FirstHeader);

    private sealed class UsbProbe
    {
        public readonly TaskCompletionSource<Handoff?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public StreamClient(IReadOnlyList<StreamEndpoint> endpoints, PacketBufferPool pool, IStreamClientListener listener,
        IUsbLink? usb = null, TimeSpan? usbCheckInterval = null)
    {
        if (endpoints.Count == 0) throw new ArgumentException("at least one endpoint", nameof(endpoints));
        _endpoints = endpoints;
        _pool = pool;
        _listener = listener;
        _usb = usb ?? new AdbForwarder();
        _usbCheckInterval = usbCheckInterval ?? TimeSpan.FromSeconds(3);
        _pingCallback = _ => SendPing();
    }

    public StreamEndpoint? ConnectedEndpoint { get; private set; }

    /// <summary>
    /// Replaces the endpoint list (e.g. the phone's Wi-Fi address changed). Takes effect on the next
    /// (re)connect; a running session is not interrupted.
    /// </summary>
    public void SetEndpoints(IReadOnlyList<StreamEndpoint> endpoints)
    {
        if (endpoints.Count == 0) throw new ArgumentException("at least one endpoint", nameof(endpoints));
        _endpoints = endpoints;
    }
    public long BytesReceived { get; private set; }

    public void Start()
    {
        if (_thread != null) throw new InvalidOperationException("already started");
        _thread = new Thread(Run) { Name = "AirLens-Rx", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _usbTimer = new Timer(_ => CheckUsb(), null, _usbCheckInterval, _usbCheckInterval);
    }

    /// <summary>
    /// Thread-safe; rate-limited to 4/s because an IDR storm is itself congestion.
    /// Returns true if a request was actually sent.
    /// </summary>
    public bool RequestKeyframe()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastKeyframeRequestTicks);
        if (Stopwatch.GetElapsedTime(last, now) < MinKeyframeInterval) return false;
        if (Interlocked.CompareExchange(ref _lastKeyframeRequestTicks, now, last) != last) return false;
        lock (_sendLock) TrySend(ClientPackets.WriteRequestKeyframe(_sendBuffer));
        return true;
    }

    public void SendReceiverReport(uint framesDecoded, uint framesDropped, ushort backlogMs)
    {
        lock (_sendLock) TrySend(ClientPackets.WriteReceiverReport(_sendBuffer, framesDecoded, framesDropped, backlogMs));
    }

    /// <summary>Thread-safe: sends a CONTROL packet (<see cref="ClientPackets.BuildControl"/>). Dropped while disconnected.</summary>
    public void SendControl(byte[] packet)
    {
        lock (_sendLock) TrySend(packet);
    }

    private void SendPing()
    {
        lock (_sendLock) TrySend(ClientPackets.WritePing(_sendBuffer, NowUs()));
    }

    private void Run()
    {
        var backoff = TimeSpan.FromMilliseconds(250);
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            foreach (var endpoint in _endpoints) // a snapshot: SetEndpoints swaps the whole list
            {
                if (token.IsCancellationRequested) break;
                var started = Stopwatch.GetTimestamp();
                string reason = RunSession(endpoint, token);
                var handoff = TakeHandoff();
                Ended(endpoint, handoff != null ? $"{handoff.Endpoint} is back, switching to it" : reason, token);
                if (handoff != null)
                {
                    started = Stopwatch.GetTimestamp();
                    Ended(handoff.Endpoint, RunSession(handoff.Endpoint, token, handoff), token);
                }
                // A session that stayed healthy for a while resets the backoff.
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5)) backoff = TimeSpan.FromMilliseconds(250);
                if (handoff != null) break; // after a USB session, start again from the top of the list
            }
            if (token.WaitHandle.WaitOne(backoff)) break;
            backoff = TimeSpan.FromMilliseconds(Math.Min(2000, backoff.TotalMilliseconds * 2));
        }
    }

    private void Ended(StreamEndpoint endpoint, string reason, CancellationToken token)
    {
        if (ConnectedEndpoint != null)
        {
            ConnectedEndpoint = null;
            _listener.OnDisconnected(endpoint, reason);
        }
        else if (!token.IsCancellationRequested)
        {
            _listener.OnStatus($"{endpoint}: {reason}");
        }
    }

    /// <summary>
    /// One session: connect + HELLO (or take over a <paramref name="handoff"/> link that already
    /// did both) + read loop. Returns why it ended; never throws.
    /// </summary>
    private string RunSession(StreamEndpoint endpoint, CancellationToken token, Handoff? handoff = null)
    {
        if (handoff == null && endpoint.Kind == EndpointKind.Usb)
        {
            var adb = _usb.Forward(endpoint.Port, endpoint.Port);
            if (!adb.Ok)
            {
                _listener.OnStatus(adb.Message);
                return adb.Message;
            }
        }

        var socket = handoff?.Socket ?? NewSocket();
        try
        {
            if (handoff == null) Connect(socket, endpoint, token);
            lock (_sendLock)
            {
                _socket = socket;
                _sessionId++;
                if (handoff == null) TrySend(ClientPackets.WriteHello(_sendBuffer, ProtocolConstants.CapHevc | ProtocolConstants.Cap4K));
            }
            handoff?.FirstHeader.CopyTo(_header, 0);
            ConnectedEndpoint = endpoint;
            _listener.OnConnected(endpoint);
            _pingTimer = new Timer(_pingCallback, null, 0, PingIntervalMs);
            using var registration = token.Register(() => SafeClose(socket));
            ReadLoop(socket, token, headerRead: handoff != null);
            return "stopped";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return $"connect to {endpoint} timed out";
        }
        catch (OperationCanceledException)
        {
            return "stopped";
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
        {
            return "phone silent for 3 s";
        }
        catch (Exception e) when (e is SocketException or IOException or ProtocolException or ObjectDisposedException)
        {
            return token.IsCancellationRequested ? "stopped" : e.Message;
        }
        finally
        {
            _pingTimer?.Dispose();
            _pingTimer = null;
            lock (_sendLock) _socket = null;
            SafeClose(socket);
        }
    }

    private static Socket NewSocket() => new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    {
        NoDelay = true,
        // Large enough to absorb a 4K IDR burst; the reader drains it immediately, so a big
        // receive buffer adds no latency.
        ReceiveBufferSize = 8 * 1024 * 1024,
        ReceiveTimeout = ReadTimeoutMs,
        SendTimeout = 1000,
    };

    private static void Connect(Socket socket, StreamEndpoint endpoint, CancellationToken token)
    {
        var target = LanRouting.Resolve(endpoint.Host);
        if (endpoint.Kind == EndpointKind.Wifi && LanRouting.SourceAddressFor(target) is { } source)
        {
            // Pin the connection to the adapter that is on the phone's subnet, so a VPN route
            // for an overlapping subnet (e.g. Tailscale) cannot capture it.
            socket.Bind(new System.Net.IPEndPoint(source, 0));
        }
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectCts.CancelAfter(ConnectTimeoutMs);
        socket.ConnectAsync(new System.Net.IPEndPoint(target, endpoint.Port), connectCts.Token).AsTask().GetAwaiter().GetResult();
    }

    // ---- back to USB -------------------------------------------------------------------------

    /// <summary>
    /// Timer callback: while on Wi-Fi with USB first in the list and a phone attached, open the USB
    /// link next to the running session, then end the Wi-Fi session so the receive thread takes it.
    /// </summary>
    private void CheckUsb()
    {
        if (Interlocked.Exchange(ref _usbChecking, 1) == 1) return;
        try
        {
            if (_cts.IsCancellationRequested) return;
            var usb = _endpoints[0];
            if (ConnectedEndpoint is not { Kind: not EndpointKind.Usb } || usb.Kind != EndpointKind.Usb) return;
            if (Stopwatch.GetTimestamp() < Interlocked.Read(ref _usbQuietUntilTicks) || !_usb.PhoneAttached()) return;

            int session;
            lock (_sendLock) session = _sessionId;
            var probe = new UsbProbe();
            Volatile.Write(ref _probe, probe); // a session that ends from here on waits for this probe
            var handoff = TryOpen(usb);
            if (handoff == null)
            {
                Interlocked.CompareExchange(ref _probe, null, probe);
                probe.Result.TrySetResult(null);
                Interlocked.Exchange(ref _usbQuietUntilTicks, Stopwatch.GetTimestamp() + (long)(UsbQuietAfterFailure.TotalSeconds * Stopwatch.Frequency));
                _listener.OnStatus($"a phone is on USB but AirLens did not answer there; staying on Wi-Fi");
                return;
            }

            bool closed;
            lock (_sendLock)
            {
                // Still the same Wi-Fi session: end it, and its end takes the USB link.
                closed = _sessionId == session && _socket != null;
                if (closed) SafeClose(_socket!);
            }
            // A newer session already runs and nobody waits for this link: drop it.
            if (!closed && _sessionId != session && Interlocked.CompareExchange(ref _probe, null, probe) == probe)
            {
                SafeClose(handoff.Socket);
                handoff = null;
            }
            probe.Result.TrySetResult(handoff);
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Best effort; the next tick tries again.
        }
        finally
        {
            Interlocked.Exchange(ref _usbChecking, 0);
        }
    }

    /// <summary>adb forward, connect, HELLO, and the first packet header; null if any step fails.</summary>
    private Handoff? TryOpen(StreamEndpoint endpoint)
    {
        if (!_usb.Forward(endpoint.Port, endpoint.Port).Ok) return null;
        var socket = NewSocket();
        try
        {
            Connect(socket, endpoint, _cts.Token);
            var hello = new byte[ClientPackets.HelloLength];
            socket.Send(hello.AsSpan(0, ClientPackets.WriteHello(hello, ProtocolConstants.CapHevc | ProtocolConstants.Cap4K)));
            // The phone answers a HELLO at once (STREAM_INFO), so a reply proves the stream is there.
            var header = new byte[ProtocolConstants.HeaderSize];
            ReceiveExactly(socket, header);
            PacketHeader.Read(header);
            return new Handoff(endpoint, socket, header);
        }
        catch (Exception e) when (e is SocketException or IOException or ProtocolException or OperationCanceledException or ObjectDisposedException)
        {
            SafeClose(socket);
            return null;
        }
    }

    /// <summary>After a session: if a USB probe is under way, wait for it and take its link.</summary>
    private Handoff? TakeHandoff()
    {
        var probe = Interlocked.Exchange(ref _probe, null);
        if (probe == null) return null;
        if (probe.Result.Task.Wait(TimeSpan.FromSeconds(12))) return probe.Result.Task.Result;
        probe.Result.Task.ContinueWith(t => { if (t.Result is { } late) SafeClose(late.Socket); }, TaskScheduler.Default);
        return null;
    }

    // ---- reading -----------------------------------------------------------------------------

    private void ReadLoop(Socket socket, CancellationToken token, bool headerRead = false)
    {
        while (!token.IsCancellationRequested)
        {
            if (!headerRead) ReceiveExactly(socket, _header);
            headerRead = false;
            var header = PacketHeader.Read(_header);
            switch (header.Type)
            {
                case PacketType.VideoFrame:
                {
                    var frame = _pool.Rent(header.PayloadLength);
                    try
                    {
                        ReceiveExactly(socket, frame.Payload);
                    }
                    catch
                    {
                        frame.Return();
                        throw;
                    }
                    frame.Flags = header.FrameFlags;
                    frame.PtsUs = header.TimestampUs;
                    frame.Sequence = header.Sequence;
                    frame.Epoch = _currentEpoch;
                    frame.ArrivalTicks = Stopwatch.GetTimestamp();
                    BytesReceived += header.PayloadLength + ProtocolConstants.HeaderSize;
                    _listener.OnFrame(frame);
                    break;
                }
                case PacketType.StreamInfo:
                {
                    var payload = ReceiveSmall(socket, header.PayloadLength);
                    var info = StreamInfo.Parse(payload);
                    _currentEpoch = info.Epoch;
                    _listener.OnStreamInfo(info);
                    break;
                }
                case PacketType.DeviceState:
                {
                    if (header.PayloadLength > ProtocolConstants.MaxDeviceState)
                        throw new ProtocolException($"DEVICE_STATE too large ({header.PayloadLength})");
                    var json = new byte[header.PayloadLength]; // rare: on connect and on lens/quality changes
                    ReceiveExactly(socket, json);
                    if (DeviceState.TryParse(json) is { } state) _listener.OnDeviceState(state);
                    else _listener.OnStatus("ignored a malformed DEVICE_STATE");
                    break;
                }
                case PacketType.Pong:
                {
                    var payload = ReceiveSmall(socket, header.PayloadLength);
                    if (payload.Length >= ProtocolConstants.PongSize)
                    {
                        long serverDwell = BinaryPrimitives.ReadInt64BigEndian(payload[8..]) - BinaryPrimitives.ReadInt64BigEndian(payload);
                        _listener.OnRoundTrip((NowUs() - header.TimestampUs - serverDwell) / 1000.0);
                    }
                    break;
                }
                default:
                    Skip(socket, header.PayloadLength); // HEARTBEAT and unknown types
                    break;
            }
        }
    }

    private Span<byte> ReceiveSmall(Socket socket, int length)
    {
        if (length > _scratch.Length) throw new ProtocolException($"control packet too large ({length})");
        var span = _scratch.AsSpan(0, length);
        ReceiveExactly(socket, span);
        return span;
    }

    private void Skip(Socket socket, int length)
    {
        while (length > 0)
        {
            int n = Math.Min(length, _scratch.Length);
            ReceiveExactly(socket, _scratch.AsSpan(0, n));
            length -= n;
        }
    }

    private static void ReceiveExactly(Socket socket, Span<byte> dst)
    {
        while (dst.Length > 0)
        {
            int n = socket.Receive(dst, SocketFlags.None);
            if (n == 0) throw new IOException("phone closed the connection");
            dst = dst[n..];
        }
    }

    /// <summary>
    /// Sends the first <paramref name="length"/> bytes of <see cref="_sendBuffer"/>. Callers hold
    /// <see cref="_sendLock"/>, which serialises the ping timer, the decoder thread and the UI.
    /// Never throws: a broken socket is noticed by the read loop, which reconnects.
    /// </summary>
    private void TrySend(int length) => TrySend(_sendBuffer.AsSpan(0, length));

    private void TrySend(ReadOnlySpan<byte> packet)
    {
        var socket = _socket;
        if (socket == null) return;
        try
        {
            socket.Send(packet);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }
    }

    private long NowUs() => (long)_clock.Elapsed.TotalMicroseconds;

    private static void SafeClose(Socket socket)
    {
        try { socket.Shutdown(SocketShutdown.Both); } catch (Exception) { }
        socket.Dispose();
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _usbTimer?.Dispose();
        lock (_sendLock)
        {
            if (_socket != null) SafeClose(_socket);
        }
        _thread?.Join(TimeSpan.FromSeconds(3));
        if (Interlocked.Exchange(ref _probe, null) is { } probe)
            probe.Result.Task.ContinueWith(t => { if (t.Result is { } h) SafeClose(h.Socket); }, TaskScheduler.Default);
        _cts.Dispose();
    }
}
