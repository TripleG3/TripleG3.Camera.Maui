namespace TripleG3.Camera.Maui.Controls;

public sealed class NewCameraView : View, IAsyncDisposable
{
    public static readonly BindableProperty CameraIdProperty =
        BindableProperty.Create(nameof(CameraId), typeof(string), typeof(NewCameraView), null, propertyChanged: OnCameraIdChanged);

    public string? CameraId
    {
        get => (string?)GetValue(CameraIdProperty);
        set => SetValue(CameraIdProperty, value);
    }

    internal INewCameraViewHandler? HandlerImpl { get; set; }

    public bool IsRunning => HandlerImpl?.IsRunning == true;

    static void OnCameraIdChanged(BindableObject bindable, object? oldValue, object? newValue)
    {
        var view = (NewCameraView)bindable;
        view.HandlerImpl?.OnCameraIdChanged((string?)newValue);
    }

    public Task StartAsync() => HandlerImpl?.StartAsync() ?? Task.CompletedTask;
    public Task StopAsync() => HandlerImpl?.StopAsync() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (HandlerImpl != null)
            await HandlerImpl.DisposeAsync();
    }
}

public interface INewCameraViewHandler : IAsyncDisposable
{
    bool IsRunning { get; }
    void OnCameraIdChanged(string? cameraId);
    Task StartAsync();
    Task StopAsync();
}
