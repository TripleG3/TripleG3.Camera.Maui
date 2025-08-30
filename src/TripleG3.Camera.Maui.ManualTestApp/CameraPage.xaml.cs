namespace TripleG3.Camera.Maui.ManualTestApp;

public partial class CameraPage : ContentPage
{
    ICameraService? _cameraService;
    IReadOnlyList<CameraInfo>? _cameras;
    bool _initialized;
    ICameraFrameBroadcaster? _broadcaster;
    IRemoteFrameDistributor? _remoteDist;
    Guid _liveSubscription;
    Guid _bufferSubscription;
    readonly Queue<CameraFrame> _bufferQueue = new();
    readonly object _bufferGate = new();
    const int MaxBufferFrames = 30; // ~1-2s depending on FPS
    bool _playBuffered;
    bool _showBuffered; // current mode

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
            CameraPicker.ItemsSource = _cameras is List<CameraInfo> list ? list : _cameras.ToList();
            if (_cameras.Count > 0)
            {
                CameraPicker.SelectedIndex = 0;
                GpuCameraView.CameraId = _cameras[0].Id; // sets default camera id
                // Auto-start preview on first appearance
                if (!GpuCameraView.IsRunning)
                    await GpuCameraView.StartAsync();
            }
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
                    MainThread.BeginInvokeOnMainThread(() => _remoteDist.Push(frame));
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
                        if (_bufferQueue.Count > 0)
                            next = _bufferQueue.Dequeue();
                    }
                    if (next != null)
                    {
                        if (_showBuffered)
                        {
                            var frame = next.Value;
                            MainThread.BeginInvokeOnMainThread(() => _remoteDist?.Push(frame));
                        }
                    }
                    await Task.Delay(66); // ~15 fps playback
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
        // Placeholder: would establish network connection using RemoteHostEntry.Text and RemotePortEntry.Text.
        // For now just ensure subscription loopback is active.
    EnsureLocalSubscription();
    DisplayAlert("Connect", "Loopback connection active (placeholder for network).", "OK");
    }

    private void FeedModePicker_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (FeedModePicker.SelectedIndex == 1)
        {
            // Buffered
            _showBuffered = true;
        }
        else
        {
            _showBuffered = false;
            // Clear buffer so next switch back starts fresh
            lock (_bufferGate) _bufferQueue.Clear();
        }
    }
}
