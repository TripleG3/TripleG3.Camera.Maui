using System.Collections.Immutable;

namespace TripleG3.Camera.Maui;

public sealed partial class CameraView : View, IAsyncDisposable
{
    public CameraView()
    {
        Loaded += async (s, e) =>
        {
            if (CameraViewHandler != null)
                await CameraViewHandler.LoadAsync();
            UpdateSize();
        };
        SizeChanged += (s, e) => UpdateSize();
    }

    internal ICameraViewHandler? CameraViewHandler { get; set; }

    public static readonly BindableProperty SelectedCameraProperty = BindableProperty.Create(nameof(SelectedCamera), typeof(CameraInfo), typeof(CameraView), CameraInfo.Empty, propertyChanged: (b, o, n) =>
    {
        if (b is CameraView cameraView && n is CameraInfo cameraInfo)
            cameraView.CameraViewHandler?.OnCameraInfoChanged(cameraInfo);
    });

    public CameraInfo SelectedCamera
    {
        get => (CameraInfo)GetValue(SelectedCameraProperty);
        set => SetValue(SelectedCameraProperty, value);
    }

    public ImmutableList<CameraInfo> CameraInfos => CameraViewHandler is null ? [] : CameraViewHandler.CameraInfos;

    public bool IsRunning => CameraViewHandler?.IsStreaming == true;
    public Task StartAsync() => CameraViewHandler?.StartAsync() ?? Task.CompletedTask;
    public Task StopAsync() => CameraViewHandler?.StopAsync() ?? Task.CompletedTask;

    private void UpdateSize()
    {
        CameraViewHandler?.OnWidthChanged(Width);
        CameraViewHandler?.OnHeightChanged(Height);
    }

    public async ValueTask DisposeAsync()
    {
        if (CameraViewHandler != null)
            await CameraViewHandler.DisposeAsync();
    }
}

public interface ICameraViewHandler : IAsyncDisposable
{
    bool IsStreaming { get; }
    void OnCameraInfoChanged(CameraInfo cameraInfo);
    void OnHeightChanged(double height);
    void OnWidthChanged(double height);
    Task StartAsync();
    Task StopAsync();
    ValueTask LoadAsync(CancellationToken cancellationToken = default);
    ImmutableList<CameraInfo> CameraInfos { get; }
}
