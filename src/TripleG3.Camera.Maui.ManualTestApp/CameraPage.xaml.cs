namespace TripleG3.Camera.Maui.ManualTestApp;

public partial class CameraPage : ContentPage
{
    ICameraService? _cameraService;
    IReadOnlyList<CameraInfo>? _cameras;
    bool _initialized;
    ICameraFrameBroadcaster? _broadcaster;
    IRemoteFrameDistributor? _remoteDist;
    // Separate distributors so Remote view shows only selected feed without overlap
    readonly RemoteFrameDistributor _liveDistributor = new();
    readonly RemoteFrameDistributor _bufferedDistributor = new();
    Guid _liveSubscription;
    Guid _bufferSubscription;
    readonly Queue<CameraFrame> _bufferQueue = new();
    readonly object _bufferGate = new();
    const int MaxBufferFrames = 60; // allow up to ~4s at 15fps
    const int MinBufferFrames = 15; // need at least ~1s before starting buffered playback
    bool _playBuffered;
    bool _showBuffered; // current mode
    bool _bufferPlaying; // indicates buffered playback has started (after threshold)

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
            _remoteDist ??= ServiceHelper.GetRequiredService<IRemoteFrameDistributor>();
            _cameras = await _cameraService.GetCamerasAsync();
            // Picker expects IList; ensure concrete list
            CameraPicker.ItemsSource = _cameras is List<CameraInfo> list ? list : [.. _cameras];
            if (_cameras.Count > 0)
            {
                CameraPicker.SelectedIndex = 0;
                GpuCameraView.CameraId = _cameras[0].Id; // sets default camera id
                // Auto-start preview on first appearance
                if (!GpuCameraView.IsRunning)
                    await GpuCameraView.StartAsync();
            }

            // Ensure initial picker selections are applied (defaults set in XAML)
            if (FeedModePicker.SelectedIndex < 0) FeedModePicker.SelectedIndex = 0; // Live
            if (ViewModePicker.SelectedIndex < 0) ViewModePicker.SelectedIndex = 0; // Local
            FeedStatusLabel.Text = "Live";
            _showBuffered = false;
            ViewModePicker_SelectedIndexChanged(ViewModePicker, EventArgs.Empty);
            FeedModePicker_SelectedIndexChanged(FeedModePicker, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Cameras", "Failed to enumerate cameras: " + ex.Message, "OK");
        }
    }

    void EnsureLocalSubscription()
    {
        if (_broadcaster == null || _remoteDist == null) return;
        if (_liveSubscription == Guid.Empty)
        {
            _liveSubscription = _broadcaster.Subscribe(frame =>
            {
                if (!_showBuffered)
                {
                    MainThread.BeginInvokeOnMainThread(() => _liveDistributor.Push(frame));
                }
            });
        }
        if (_bufferSubscription == Guid.Empty)
        {
            _bufferSubscription = _broadcaster.Subscribe(frame =>
            {
                lock (_bufferGate)
                {
                    _bufferQueue.Enqueue(frame);
                    while (_bufferQueue.Count > MaxBufferFrames) _bufferQueue.Dequeue();
                }
            });
        }
        // Kick off buffered playback timer once
        if (!_playBuffered)
        {
            _playBuffered = true;
            _ = Task.Run(async () =>
            {
                while (_playBuffered)
                {
                    CameraFrame? next = null;
                    lock (_bufferGate)
                    {
                        if (_showBuffered)
                        {
                            // Start playing only after threshold to create noticeable latency window
                            if (!_bufferPlaying && _bufferQueue.Count >= MinBufferFrames)
                                _bufferPlaying = true;
                            if (_bufferPlaying && _bufferQueue.Count > 0)
                                next = _bufferQueue.Dequeue();
                        }
                        else
                        {
                            // If switched back to live, reset buffered playback state
                            _bufferPlaying = false;
                            _bufferQueue.Clear();
                        }
                    }
                    if (next != null && _showBuffered)
                    {
                        if (_showBuffered)
                        {
                            var frame = next.Value;
                            MainThread.BeginInvokeOnMainThread(() => _bufferedDistributor.Push(frame));
                        }
                    }
                    await Task.Yield();
                }
            });
        }
    }

    private async void CameraPicker_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_cameras == null) return;
        var idx = CameraPicker.SelectedIndex;
        if (idx < 0 || idx >= _cameras.Count) return;
        var selected = _cameras[idx];
        if (GpuCameraView.CameraId == selected.Id) return; // no change

        var wasRunning = GpuCameraView.IsRunning;
        if (wasRunning)
            await GpuCameraView.StopAsync();

        GpuCameraView.CameraId = selected.Id; // will trigger handler camera change (restart logic inside handler)

        if (wasRunning)
            await GpuCameraView.StartAsync();
    }

    private async void OnStartClicked(object sender, EventArgs e)
    {
        if (!GpuCameraView.IsRunning)
        {
            await GpuCameraView.StartAsync();
            EnsureLocalSubscription();
        }
    }

    private async void OnStopClicked(object sender, EventArgs e)
    {
        if (GpuCameraView.IsRunning)
            await GpuCameraView.StopAsync();
    }

    private void OnConnectClicked(object sender, EventArgs e)
    {
        // Apply entered network endpoint to RemoteVideoView; it will auto (re)start its receiver.
        RemoteView.IpAddress = RemoteHostEntry.Text;
        if (int.TryParse(RemotePortEntry.Text, out var port))
            RemoteView.Port = port;
        if (ProtocolPicker.SelectedIndex >= 0)
            RemoteView.Protocol = (RemoteVideoProtocol)ProtocolPicker.SelectedIndex; // enum order matches picker items
        // Optional: keep local subscription active so user can still view local feed when switching modes.
        EnsureLocalSubscription();
        DisplayAlert("Remote", $"Configured {RemoteView.Protocol} {RemoteView.IpAddress}:{RemoteView.Port}", "OK");
    }

    private void FeedModePicker_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (FeedModePicker.SelectedIndex == 1)
        {
            // Buffered
            _showBuffered = true;
            _bufferPlaying = false; // force re-buffer
            FeedStatusLabel.Text = "Buffering...";
        }
        else
        {
            _showBuffered = false;
            // Clear buffer so next switch back starts fresh
            lock (_bufferGate) _bufferQueue.Clear();
            _bufferPlaying = false;
            FeedStatusLabel.Text = "Live";
            // Immediately show latest live frame stream
            _bufferedDistributor.UnregisterSink(OnBufferedFrame);
            _liveDistributor.UnregisterSink(OnLiveFrame);
            _liveDistributor.RegisterSink(OnLiveFrame);
        }
    }

    private void ViewModePicker_SelectedIndexChanged(object sender, EventArgs e)
    {
        var remote = RemoteView;
        var local = GpuCameraView;
        if (ViewModePicker.SelectedIndex == 1)
        {
            // Remote
            remote.IsVisible = true;
            local.IsVisible = false;
            // Attach appropriate sink based on current mode
            _liveDistributor.UnregisterSink(OnLiveFrame);
            _bufferedDistributor.UnregisterSink(OnBufferedFrame);
            if (_showBuffered)
                _bufferedDistributor.RegisterSink(OnBufferedFrame);
            else
                _liveDistributor.RegisterSink(OnLiveFrame);
        }
        else
        {
            remote.IsVisible = false;
            local.IsVisible = true;
            _liveDistributor.UnregisterSink(OnLiveFrame);
            _bufferedDistributor.UnregisterSink(OnBufferedFrame);
        }
    }

    void OnLiveFrame(CameraFrame frame)
    {
        // Pass to platform handler via shared global distributor also (kept for network placeholder)
        _remoteDist?.Push(frame);
    }

    void OnBufferedFrame(CameraFrame frame)
    {
        _remoteDist?.Push(frame);
        if (_showBuffered && _bufferPlaying && FeedStatusLabel.Text != "Buffered")
            FeedStatusLabel.Text = "Buffered";
    }
}
