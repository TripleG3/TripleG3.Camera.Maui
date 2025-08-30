namespace TripleG3.Camera.Maui; // CHANGED from .Controls

public sealed class CameraView : View, IAsyncDisposable
{
    public CameraView()
    {
        Loaded += (s, e) =>
        {
            NewCameraViewHandler?.OnWidthChanged(Width);
            NewCameraViewHandler?.OnHeightChanged(Height);
        };
        SizeChanged += (s, e) =>
        {
            NewCameraViewHandler?.OnWidthChanged(Width);
            NewCameraViewHandler?.OnHeightChanged(Height);
        };
    }
    public static readonly BindableProperty CameraIdProperty =
        BindableProperty.Create(nameof(CameraId), typeof(string), typeof(CameraView), null, propertyChanged: OnCameraIdChanged);

    public string? CameraId
    {
        get => (string?)GetValue(CameraIdProperty);
        set => SetValue(CameraIdProperty, value);
    }

    internal INewCameraViewHandler? NewCameraViewHandler { get; set; }
    public bool IsRunning => NewCameraViewHandler?.IsRunning == true;

    static void OnCameraIdChanged(BindableObject bindable, object? oldValue, object? newValue) =>
        ((CameraView)bindable).NewCameraViewHandler?.OnCameraIdChanged((string?)newValue);

    public Task StartAsync() => NewCameraViewHandler?.StartAsync() ?? Task.CompletedTask;
    public Task StopAsync()  => NewCameraViewHandler?.StopAsync()  ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (NewCameraViewHandler != null)
            await NewCameraViewHandler.DisposeAsync();
    }
}

public interface INewCameraViewHandler : IAsyncDisposable
{
    bool IsRunning { get; }
    void OnCameraIdChanged(string? cameraId);
    void OnHeightChanged(double height);
    void OnWidthChanged(double height);
    Task StartAsync();
    Task StopAsync();
}
