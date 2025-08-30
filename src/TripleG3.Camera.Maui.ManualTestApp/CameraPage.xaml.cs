namespace TripleG3.Camera.Maui.ManualTestApp;

public partial class CameraPage : ContentPage
{
    public CameraPage()
    {
        InitializeComponent();
        GpuCameraView.Loaded += (s, e) =>
        {
            CameraPicker.SetBinding(Picker.ItemsSourceProperty, new Binding(nameof(GpuCameraView.CameraInfos), mode: BindingMode.OneWay, source: GpuCameraView));
            //CameraPicker.ItemsSource = GpuCameraView.CameraInfos;
            if (GpuCameraView.CameraInfos.Count > 0)
            {
                CameraPicker.SelectedItem = GpuCameraView.CameraInfos[0];
                GpuCameraView.SelectedCamera = GpuCameraView.CameraInfos[0];
            }
        };
    }

    private void OnStartClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        _ = GpuCameraView.StartAsync();
#endif
    }

    private void OnStopClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        _ = GpuCameraView.StopAsync();
#endif
    }

    private void CameraPicker_SelectedIndexChanged(object sender, EventArgs e)
    {
#if WINDOWS
        if (CameraPicker.SelectedItem is CameraInfo cameraInfo)
        {
            GpuCameraView.SelectedCamera = cameraInfo;
        }
#endif
    }
}
