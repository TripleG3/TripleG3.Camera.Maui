using TripleG3.Camera.Maui;
using Microsoft.Maui.Graphics;
using SkiaSharp;
using System.Threading.Channels;
using System.Runtime.InteropServices;

namespace TripleG3.Camera.Maui.ManualTestApp;

public partial class CameraPage : ContentPage, IAsyncDisposable
{
    enum RenderMode { SoftwareBitmap, DirectGpu, SwapChain, EncodedStream, FastPreview, EncodedAsync }

    readonly CameraManager _manager;
    CameraHelper? _helper;
    CameraHelper? _gpuHelper; // fallback helper for non-Windows DirectGpu

    GraphicsView? _swapView;
    RawDrawable? _swapDrawable; // used for SwapChain placeholder

    Image? _encodedImage;
    int _encodeThrottle;

    bool _loaded;
    RenderMode _mode = RenderMode.SoftwareBitmap;

    readonly string _logPath = Path.Combine(FileSystem.CacheDirectory, "CameraPerf.log");
    StreamWriter? _log;
    long _frameCount;
    DateTime _modeStart;
    long _lastFrameTicks;
    CancellationTokenSource? _cycleCts;
    List<RenderMode> _cycleOrder = new();
    int _cycleIndex;
    bool _autoTesting;

    SKBitmap? _sharedBitmap;
    Task? _encodeWorker;
    readonly Channel<CameraFrame> _encodeChannel = Channel.CreateUnbounded<CameraFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    CancellationTokenSource? _encodeCts;

    int _swapChainFrameCounter;

#if WINDOWS
    Image? _gpuImage;
#endif

    public CameraPage()
    {
        InitializeComponent();
        _manager = Camera.CreateManager();
#if WINDOWS
        if (_manager is WindowsCameraManager wcm) wcm.Logger = Log;
#endif
        Loaded += CameraPage_Loaded;
        Unloaded += CameraPage_Unloaded;
        foreach (var name in Enum.GetNames(typeof(RenderMode)))
            ModePicker.Items.Add(name);
        ModePicker.SelectedIndex = 0;
    }

    async void CameraPage_Loaded(object? s, EventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _log = new StreamWriter(File.Open(_logPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        Log($"App start {DateTime.UtcNow:O}");
        var cams = await _manager.GetCamerasAsync();
        foreach (var c in cams) CameraPicker.Items.Add(c.Name);
        if (cams.Count > 0) CameraPicker.SelectedIndex = 0;
        _cycleOrder = Enum.GetValues<RenderMode>().ToList();
        _autoTesting = true;
        _cycleCts = new CancellationTokenSource();
        _ = RunCycleAsync(_cycleCts.Token);
    }

    async Task RunCycleAsync(CancellationToken token)
    {
        while (_cycleIndex < _cycleOrder.Count && !token.IsCancellationRequested)
        {
            _mode = _cycleOrder[_cycleIndex];
            ModePicker.SelectedIndex = (int)_mode;
            Log($"MODE_START {_mode} {DateTime.UtcNow:O}");
            _frameCount = 0; _lastFrameTicks = 0; _modeStart = DateTime.UtcNow; _swapChainFrameCounter = 0;
            await StartModeAsync(_mode);
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch { }
            await StopVisualsOnlyAsync();
            var elapsed = DateTime.UtcNow - _modeStart;
            var fps = elapsed.TotalSeconds > 0 ? _frameCount / elapsed.TotalSeconds : 0;
            Log($"MODE_END {_mode} Frames={_frameCount} ElapsedMs={(int)elapsed.TotalMilliseconds} FPS={fps:F2}");
            _cycleIndex++;
        }
        Log("CYCLE_COMPLETE");
        await _manager.StopAsync();
        _autoTesting = false;
    }

    async Task StartModeAsync(RenderMode mode)
    {
        if (CameraPicker.SelectedIndex < 0) return;
        var cams = await _manager.GetCamerasAsync();
        if (CameraPicker.SelectedIndex >= cams.Count) return;
        var cam = cams[CameraPicker.SelectedIndex];
#if WINDOWS
        if (_manager is WindowsCameraManager wcmReset)
        {
            wcmReset.FastPreview = false;
            wcmReset.TimestampObserver = null;
        }
#endif
        await _manager.StopAsync();
        switch (mode)
        {
            case RenderMode.SoftwareBitmap:
                _helper = new CameraHelper(_manager) { PixelStep = 2 };
                _helper.FrameObserved = f => CountFrame(f.TimestampUtcTicks);
                PreviewHost.Content = _helper.View;
                await _helper.StartAsync(cam.Id);
                break;
            case RenderMode.DirectGpu:
                _gpuHelper = new CameraHelper(_manager) { PixelStep = 1 }; // unified path
                _gpuHelper.FrameObserved = f => CountFrame(f.TimestampUtcTicks);
                PreviewHost.Content = _gpuHelper.View;
                await _gpuHelper.StartAsync(cam.Id);
                break;
            case RenderMode.SwapChain:
                _swapDrawable = new RawDrawable();
                _swapView = new GraphicsView { Drawable = _swapDrawable };
                PreviewHost.Content = _swapView;
                await _manager.SelectCameraAsync(cam.Id);
                await _manager.StartAsync(OnFrameSwapChainAsync);
                Device.StartTimer(TimeSpan.FromSeconds(1), () =>
                {
                    if (_mode == RenderMode.SwapChain && _swapChainFrameCounter == 0)
                    {
                        Log("SWAPCHAIN_FALLBACK activating helper preview due to no frames");
                        _helper = new CameraHelper(_manager) { PixelStep = 2 };
                        _helper.FrameObserved = f => CountFrame(f.TimestampUtcTicks);
                        PreviewHost.Content = _helper.View;
                        _manager.SetFrameCallback(f => { CountFrame(f.TimestampUtcTicks); return ValueTask.CompletedTask; });
                    }
                    return false;
                });
                break;
            case RenderMode.EncodedStream:
                _encodedImage = new Image { Aspect = Aspect.AspectFit };
                PreviewHost.Content = _encodedImage;
                _encodeThrottle = 0;
                await _manager.SelectCameraAsync(cam.Id);
                await _manager.StartAsync(OnFrameEncodedAsync);
                break;
            case RenderMode.FastPreview:
#if WINDOWS
                if (_manager is WindowsCameraManager wcm)
                {
                    wcm.FastPreview = true;
                    wcm.TimestampObserver = CountFrame;
                }
#endif
                PreviewHost.Content = new Label { Text = "Fast Preview (no rendering)", HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
                await _manager.SelectCameraAsync(cam.Id);
                await _manager.StartAsync(_ => ValueTask.CompletedTask);
                break;
            case RenderMode.EncodedAsync:
                _encodedImage = new Image { Aspect = Aspect.AspectFit };
                PreviewHost.Content = _encodedImage;
                StartEncodeWorker();
                await _manager.SelectCameraAsync(cam.Id);
                await _manager.StartAsync(OnFrameEncodedAsyncBackground);
                break;
        }
    }

    ValueTask OnFrameSwapChainAsync(CameraFrame frame)
    {
        _swapChainFrameCounter++;
        CountFrame(frame.TimestampUtcTicks);
        _swapDrawable?.Update(frame);
        MainThread.BeginInvokeOnMainThread(() => _swapView?.Invalidate());
        return ValueTask.CompletedTask;
    }

    void StartEncodeWorker()
    {
        _encodeCts?.Cancel();
        _encodeCts = new CancellationTokenSource();
        var token = _encodeCts.Token;
        _encodeWorker = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in _encodeChannel.Reader.ReadAllAsync(token))
                {
                    if (_sharedBitmap == null || _sharedBitmap.Width != frame.Width || _sharedBitmap.Height != frame.Height)
                    {
                        _sharedBitmap?.Dispose();
                        _sharedBitmap = new SKBitmap(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    }
                    Marshal.Copy(frame.Data, 0, _sharedBitmap.GetPixels(), frame.Data.Length);
                    using var data = _sharedBitmap.Encode(SKEncodedImageFormat.Jpeg, 60);
                    var bytes = data.ToArray();
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (_encodedImage != null)
                            _encodedImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
                    });
                }
            }
            catch { }
        }, token);
    }

    ValueTask OnFrameEncodedAsyncBackground(CameraFrame frame)
    {
        CountFrame(frame.TimestampUtcTicks);
        if (_frameCount % 10 == 0)
            _encodeChannel.Writer.TryWrite(frame);
        return ValueTask.CompletedTask;
    }

    ValueTask OnFrameEncodedAsync(CameraFrame frame)
    {
        CountFrame(frame.TimestampUtcTicks);
        if (++_encodeThrottle % 15 != 0) return ValueTask.CompletedTask;
        try
        {
            using var skBmp = new SKBitmap(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            Marshal.Copy(frame.Data, 0, skBmp.GetPixels(), frame.Data.Length);
            using var data = skBmp.Encode(SKEncodedImageFormat.Jpeg, 60);
            var bytes = data.ToArray();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_encodedImage != null)
                    _encodedImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
            });
        }
        catch { }
        return ValueTask.CompletedTask;
    }

    void CountFrame(long cameraTicks)
    {
        _frameCount++;
        if (_frameCount % 30 == 0)
        {
            long deltaTicks = _lastFrameTicks == 0 ? 0 : cameraTicks - _lastFrameTicks;
            Log($"FRAME mode={_mode} count={_frameCount} camTs={cameraTicks} deltaTicks={deltaTicks}");
        }
        _lastFrameTicks = cameraTicks;
    }

    async void CameraPage_Unloaded(object? s, EventArgs e)
    {
        _cycleCts?.Cancel();
        await DisposeAsync();
    }

    async void OnStartClicked(object? sender, EventArgs e)
    {
        _cycleCts?.Cancel();
        _autoTesting = false;
        _mode = (RenderMode)ModePicker.SelectedIndex;
        await StartModeAsync(_mode);
#if WINDOWS
        _ = GpuCameraView.StartAsync();
#endif
    }

    async void OnStopClicked(object? sender, EventArgs e)
    {
        await StopVisualsOnlyAsync();
#if WINDOWS
        _ = GpuCameraView.StopAsync();
#endif
    }

    async Task StopVisualsOnlyAsync()
    {
        try { PreviewHost.Content = null; } catch { }
        _helper = null; _gpuHelper = null; _swapView = null; _swapDrawable = null; _encodedImage = null;
        _encodeCts?.Cancel(); _sharedBitmap?.Dispose(); _sharedBitmap = null;
#if WINDOWS
        _gpuImage = null;
        if (_manager is WindowsCameraManager wcm) { wcm.FastPreview = false; wcm.TimestampObserver = null; }
#endif
    }

    async Task StopAllAsync()
    {
        try { await _manager.StopAsync(); await StopVisualsOnlyAsync(); } catch { }
    }

    void Log(string line)
    {
        try { _log?.WriteLine(line); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
        try { _log?.Dispose(); } catch { }
    }

    sealed class RawDrawable : IDrawable
    {
        byte[] _data = Array.Empty<byte>();
        int _w; int _h; readonly object _sync = new();
        public void Update(CameraFrame frame)
        {
            if (frame.PixelFormat != CameraPixelFormat.Bgra32 && frame.PixelFormat != CameraPixelFormat.Rgba32) return;
            lock (_sync)
            {
                if (_data.Length != frame.Data.Length) _data = new byte[frame.Data.Length];
                Buffer.BlockCopy(frame.Data, 0, _data, 0, frame.Data.Length);
                _w = frame.Width; _h = frame.Height;
            }
        }
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            byte[] local; int w; int h;
            lock (_sync) { if (_w == 0 || _h == 0) return; local = _data; w = _w; h = _h; }
            int stride = 4 * w; float sx = dirtyRect.Width / w; float sy = dirtyRect.Height / h;
            int step = 2; bool bgra = true;
            for (int y = 0; y < h; y += step)
            {
                int row = y * stride;
                for (int x = 0; x < w; x += step)
                {
                    int idx = row + x * 4; if (idx + 3 >= local.Length) break;
                    byte b = local[idx + (bgra ? 0 : 2)]; byte g = local[idx + 1]; byte r = local[idx + (bgra ? 2 : 0)]; byte a = local[idx + 3];
                    canvas.FillColor = Color.FromRgba(r, g, b, a);
                    canvas.FillRectangle(dirtyRect.X + x * sx, dirtyRect.Y + y * sy, step * sx, step * sy);
                }
            }
        }
    }
}
