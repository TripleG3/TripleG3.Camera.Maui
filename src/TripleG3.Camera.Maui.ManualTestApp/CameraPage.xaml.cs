using TripleG3.Camera.Maui;

namespace TripleG3.Camera.Maui.ManualTestApp;

public partial class CameraPage : ContentPage, IAsyncDisposable
{
    readonly CameraManager _manager;
    CameraHelper? _helper;
    bool _loaded;

    public CameraPage()
    {
        InitializeComponent();
        _manager = Camera.CreateManager();
        Loaded += CameraPage_Loaded;
        Unloaded += CameraPage_Unloaded;
    }

    private async void CameraPage_Loaded(object? sender, EventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        var cams = await _manager.GetCamerasAsync();
        foreach (var c in cams)
            CameraPicker.Items.Add(c.Name);
        if (cams.Count > 0)
            CameraPicker.SelectedIndex = 0;
    }

    private async void CameraPage_Unloaded(object? sender, EventArgs e)
    {
        await DisposeAsync();
    }

    async void OnStartClicked(object? sender, EventArgs e)
    {
        if (_helper != null) return;
        var cams = await _manager.GetCamerasAsync();
        if (CameraPicker.SelectedIndex < 0 || CameraPicker.SelectedIndex >= cams.Count) return;
        var selected = cams[CameraPicker.SelectedIndex];
        _helper = new CameraHelper(_manager);
        PreviewHost.Content = _helper.View;
        await _helper.StartAsync(selected.Id);
    }

    async void OnStopClicked(object? sender, EventArgs e)
    {
        if (_helper == null) return;
        await _helper.StopAsync();
        await _helper.DisposeAsync();
        PreviewHost.Content = null;
        _helper = null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_helper != null)
            {
                await _helper.StopAsync();
                await _helper.DisposeAsync();
                _helper = null;
            }
            await _manager.StopAsync();
        }
        catch { }
    }
}
