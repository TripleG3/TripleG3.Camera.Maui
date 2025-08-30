namespace TripleG3.Camera.Maui.ManualTestApp;

public partial class CameraPage : ContentPage
{
    ICameraService? _cameraService;
    IReadOnlyList<CameraInfo>? _cameras;
    bool _initialized;
    ICameraFrameBroadcaster? _broadcaster;
    IRemoteFrameDistributor? _remoteDist;
    Guid _localSubscription;

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
        if (_localSubscription != Guid.Empty) return;
        _localSubscription = _broadcaster.Subscribe(frame =>
        {
            // In real scenario, serialize + send over network. For now, loopback to remote distributor if host/port set.
            MainThread.BeginInvokeOnMainThread(() => _remoteDist.Push(frame));
        });
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
}
