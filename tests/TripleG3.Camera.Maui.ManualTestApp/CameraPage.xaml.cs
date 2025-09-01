namespace TripleG3.Camera.Maui.ManualTestApp;

using TripleG3.Camera.Maui.Streaming; // internal RTP sender stub (InternalsVisibleTo)

public partial class CameraPage : ContentPage
{
    bool _initialized;
    ICameraService? _cameraService;
    ICameraFrameBroadcaster? _broadcaster;
    Guid _subscription;

    // RTP sender plumbing
    VideoRtpSenderStub? _rtpSender; // concrete type for dispose access
    volatile bool _rtpInitialized;
    int _currentPort; // track port to rebuild sender on change
    long _sentFrames;
    long _receivedFrames; // updated via reflection path hooking into RemoteVideoView handler
    DateTime _lastReceive = DateTime.MinValue;
    CancellationTokenSource? _uiCts;
    volatile bool _mirrorEnabled = true;

    public CameraPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_initialized) return;
        _initialized = true;
        try
        {
            _cameraService ??= ServiceHelper.GetRequiredService<ICameraService>();
            _broadcaster ??= ServiceHelper.GetRequiredService<ICameraFrameBroadcaster>();
            var cams = await _cameraService.GetCamerasAsync();
            if (cams.Count > 0)
            {
                GpuCameraView.CameraId = cams[0].Id;
                await GpuCameraView.StartAsync();
            }

            // Configure RemoteVideoView to passively listen for RTP (loopback by default)
            RemoteView.Protocol = RemoteVideoProtocol.RTP;
            _currentPort = int.TryParse(RemotePortEntry.Text, out var p) ? p : 50555;
            RemoteView.Port = _currentPort;
            // Accept any source (null) or filter if user provided host
            RemoteView.IpAddress = string.IsNullOrWhiteSpace(RemoteHostEntry.Text) ? null : RemoteHostEntry.Text;

            // Create RTP sender targeting loopback (or provided host if specified) -> RemoteVideoView
            var sendHost = string.IsNullOrWhiteSpace(RemoteHostEntry.Text) ? "127.0.0.1" : RemoteHostEntry.Text.Trim();
            _rtpSender = new VideoRtpSenderStub(sendHost, _currentPort);

            // Subscribe once to broadcaster: route frames through RTP path (network loopback) instead of direct injection
            if (_subscription == Guid.Empty && _broadcaster != null)
            {
                _subscription = _broadcaster.Subscribe(frame =>
                {
                    try
                    {
                        // Lazy initialize RTP session (width/height/bitrate can be refined later)
                        if (!_rtpInitialized && _rtpSender != null)
                        {
                            _rtpInitialized = true;
                            // Fire-and-forget async init; subsequent frames after init succeed, first may be dropped.
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _rtpSender.InitializeAsync(new VideoRtpSessionConfig(frame.Width, frame.Height, 30, 2_000_000));
                                }
                                catch { _rtpInitialized = false; }
                            });
                        }
                        _rtpSender?.SubmitRawFrame(frame);
                        // Always direct inject for now (RTP path experimental). Apply mirror transform if enabled.
                        var toDisplay = _mirrorEnabled ? MirrorFrameIfNeeded(frame) : frame;
                        MainThread.BeginInvokeOnMainThread(() => RemoteView.SubmitFrame(toDisplay));
                        Interlocked.Increment(ref _receivedFrames);
                        _lastReceive = DateTime.UtcNow;
                        var sf = Interlocked.Increment(ref _sentFrames);
                        if (sf % 30 == 0) UpdateStatus();
                    }
                    catch { }
                });
            }

            // Kick off UI status updater & receive monitor
            _uiCts = new CancellationTokenSource();
            _ = Task.Run(StatusLoopAsync);
            StartReceiveProbe();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Cameras", "Failed to initialize: " + ex.Message, "OK");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        try
        {
            if (_subscription != Guid.Empty && _broadcaster != null)
            {
                _broadcaster.Unsubscribe(_subscription);
                _subscription = Guid.Empty;
            }
        }
        catch { }
    try { _rtpSender?.Dispose(); } catch { }
        _rtpSender = null;
        _rtpInitialized = false;
    try { _uiCts?.Cancel(); } catch { }
    try { if (GpuCameraView.IsRunning) _ = GpuCameraView.StopAsync(); } catch { }
    }

    private void OnConnectClicked(object sender, EventArgs e)
    {
        // Allow user to change port/host; rebuild RTP sender accordingly.
        var newPort = int.TryParse(RemotePortEntry.Text, out var port) ? port : _currentPort;
        var hostFilter = string.IsNullOrWhiteSpace(RemoteHostEntry.Text) ? null : RemoteHostEntry.Text.Trim();
        RemoteView.IpAddress = hostFilter; // filter incoming if provided
        if (newPort != _currentPort)
        {
            _currentPort = newPort;
            RemoteView.Port = _currentPort; // triggers restart internally
        }
        RemoteView.Protocol = RemoteVideoProtocol.RTP; // ensure mode

        // Recreate sender pointing at new host/port
    try { _rtpSender?.Dispose(); } catch { }
        var sendHost = hostFilter ?? "127.0.0.1";
        _rtpSender = new VideoRtpSenderStub(sendHost, _currentPort);
        _rtpInitialized = false; // will re-init on next frame

        DisplayAlert("Remote", $"RTP loopback -> {sendHost}:{_currentPort}", "OK");
    }

    private async void OnRestartCameraClicked(object sender, EventArgs e)
    {
        if (_cameraService == null) return;
        try
        {
            if (GpuCameraView.IsRunning)
                await GpuCameraView.StopAsync();
            var cams = await _cameraService.GetCamerasAsync();
            if (cams.Count > 0)
            {
                GpuCameraView.CameraId = cams[0].Id;
                await GpuCameraView.StartAsync();
            }
            _rtpInitialized = false; // force session re-init with new dimensions if they changed
        }
        catch (Exception ex)
        {
            await DisplayAlert("Restart Camera", ex.Message, "OK");
        }
    }

    private void OnMirrorToggled(object sender, ToggledEventArgs e)
    {
    _mirrorEnabled = e.Value;
    UpdateStatus();
    }

    private async void OnStopClicked(object sender, EventArgs e)
    {
        try
        {
            MirrorSwitch.IsToggled = false;
            if (_subscription != Guid.Empty && _broadcaster != null)
            {
                _broadcaster.Unsubscribe(_subscription);
                _subscription = Guid.Empty;
            }
            if (GpuCameraView.IsRunning)
                await GpuCameraView.StopAsync();
            try { _rtpSender?.Dispose(); } catch { }
            _rtpSender = null;
            _rtpInitialized = false;
            Interlocked.Exchange(ref _sentFrames, 0);
            StatusLabel.Text += " | Stopped";
        }
        catch (Exception ex)
        {
            await DisplayAlert("Stop", ex.Message, "OK");
        }
    }

    async Task StatusLoopAsync()
    {
        while (!_uiCts?.IsCancellationRequested ?? false)
        {
            try
            {
                UpdateStatus();
                // If we haven't received anything within 3s after sending frames, enable direct local view injection as fallback diagnostic
                if (_sentFrames > 0 && _receivedFrames == 0 && (DateTime.UtcNow - _lastReceive) > TimeSpan.FromSeconds(3))
                {
                    // Direct submit one latest frame from broadcaster cache (not exposed yet) -> skip (placeholder)
                    StatusLabel.Text += " | Fallback: no RTP yet";
                }
            }
            catch { }
            await Task.Delay(1000);
        }
    }

    void UpdateStatus()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = $"Sent: {_sentFrames}  Recv: {_receivedFrames}  RTP Init: {_rtpInitialized}  LastRecv: {( _lastReceive == DateTime.MinValue ? "-" : (DateTime.UtcNow - _lastReceive).TotalSeconds.ToString("0.0") + "s ago")}";
        });
    }
    // Hook into RemoteVideoView updates by wrapping its handler (only if running on supported platform)
    void StartReceiveProbe()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            byte[]? last = null;
            while (!(_uiCts?.IsCancellationRequested ?? true))
            {
                try
                {
                    var handler = RemoteView?.Handler;
                    if (handler != null)
                    {
                        var implField = handler.GetType().GetField("_latest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        if (implField != null)
                        {
                            var val = implField.GetValue(handler) as byte[];
                            if (val != null && !ReferenceEquals(val, last))
                            {
                                last = val;
                                Interlocked.Increment(ref _receivedFrames);
                                _lastReceive = DateTime.UtcNow;
                                if (_receivedFrames % 30 == 0) UpdateStatus();
                            }
                        }
                    }
                }
                catch { }
                await Task.Delay(250);
            }
        });
    }

    static CameraFrame MirrorFrameIfNeeded(CameraFrame frame)
    {
        if (frame.Format != CameraPixelFormat.BGRA32) return frame; // mirror only BGRA32
        var src = frame.Data;
        var w = frame.Width; var h = frame.Height;
        var rowBytes = w * 4;
        var dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            int rowStart = y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                int srcIndex = rowStart + x * 4;
                int dstIndex = rowStart + (w - 1 - x) * 4;
                dst[dstIndex + 0] = src[srcIndex + 0];
                dst[dstIndex + 1] = src[srcIndex + 1];
                dst[dstIndex + 2] = src[srcIndex + 2];
                dst[dstIndex + 3] = src[srcIndex + 3];
            }
        }
        return new CameraFrame(frame.Format, frame.Width, frame.Height, frame.TimestampTicks, frame.Mirrored, dst);
    }
}
