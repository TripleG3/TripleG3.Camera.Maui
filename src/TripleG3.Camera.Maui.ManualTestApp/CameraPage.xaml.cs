namespace TripleG3.Camera.Maui.ManualTestApp;

public partial class CameraPage : ContentPage
{
    private CameraManager _manager;

    public CameraPage()
    {
        InitializeComponent();
        _manager = Camera.CreateManager();
        Loaded += async (s, e) =>
        {
            var cams = await _manager.GetCamerasAsync();
            foreach (var c in cams) CameraPicker.Items.Add(c.Name);
            if (cams.Count > 0) CameraPicker.SelectedIndex = 0;
        };
    }

    void OnStartClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        _ = GpuCameraView.StartAsync();
#endif
    }

    void OnStopClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        _ = GpuCameraView.StopAsync();
#endif
    }

    private async void CameraPicker_SelectedIndexChanged(object sender, EventArgs e)
    {
#if WINDOWS
        var available = await _manager.GetCamerasAsync();
        var selected = CameraPicker.SelectedIndex >= 0 && CameraPicker.SelectedIndex < available.Count
            ? available[CameraPicker.SelectedIndex]
            : null;
        //_ = GpuCameraView.CameraId = selected?.Id;
#endif
    }
}
