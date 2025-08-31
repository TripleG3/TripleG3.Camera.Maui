namespace TripleG3.Camera.Maui; // CHANGED from .Controls

public sealed class CameraView : View, IAsyncDisposable
{
    internal bool RequestedStart { get; private set; }
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

    public static readonly BindableProperty IsMirroredProperty =
        BindableProperty.Create(nameof(IsMirrored), typeof(bool), typeof(CameraView), true, propertyChanged: OnIsMirroredChanged);

    public string? CameraId
    {
        get => (string?)GetValue(CameraIdProperty);
        set => SetValue(CameraIdProperty, value);
    }

    public bool IsMirrored
    {
        get => (bool)GetValue(IsMirroredProperty);
        set => SetValue(IsMirroredProperty, value);
    }

    internal INewCameraViewHandler? NewCameraViewHandler { get; set; }
    public bool IsRunning => NewCameraViewHandler?.IsRunning == true;

    static void OnCameraIdChanged(BindableObject bindable, object? oldValue, object? newValue) =>
        ((CameraView)bindable).NewCameraViewHandler?.OnCameraIdChanged((string?)newValue);

    static void OnIsMirroredChanged(BindableObject bindable, object? oldValue, object? newValue) =>
        ((CameraView)bindable).NewCameraViewHandler?.OnMirrorChanged((bool)(newValue ?? false));

    public Task StartAsync()
    {
        if (NewCameraViewHandler is null)
        {
            // Defer until handler is created
            RequestedStart = true;
            return Task.CompletedTask;
        }
        RequestedStart = false;
        return NewCameraViewHandler.StartAsync();
    }
    public Task StopAsync() => NewCameraViewHandler?.StopAsync() ?? Task.CompletedTask;

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
    void OnMirrorChanged(bool isMirrored);
    Task StartAsync();
    Task StopAsync();
}
