using Microsoft.Maui;
using Microsoft.Maui.Controls; // for GraphicsView
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;

namespace TripleG3.Camera.Maui;

/// <summary>
/// Helper that connects a CameraManager stream to a GraphicsView for simple preview rendering.
/// This implementation uses a coarse pixel block painting approach (not optimized) and currently
/// supports Bgra32 and Rgba32 frame formats. Other formats are ignored.
/// </summary>
public sealed class CameraHelper : IDrawable, IAsyncDisposable
{
    readonly CameraManager _manager;
    readonly GraphicsView _graphicsView;
    readonly object _frameLock = new();
    CameraFrame _latestFrame = CameraFrame.Empty; // frame reference replaced atomically under lock

    // Adjustable pixel skip to reduce draw cost (1 = full resolution, 2 = quarter, etc.)
    public int PixelStep { get; set; } = 2;

    public GraphicsView View => _graphicsView;

    public CameraHelper(CameraManager manager)
    {
        _manager = manager;
        _graphicsView = new GraphicsView
        {
            Drawable = this
        };
    }

    /// <summary>
    /// Starts the camera preview by selecting the camera and starting streaming.
    /// </summary>
    public async Task StartAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        await _manager.SelectCameraAsync(cameraId, cancellationToken);
        await _manager.StartAsync(OnFrameAsync, cancellationToken);
    }

    /// <summary>
    /// Stops the camera preview.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default) => _manager.StopAsync(cancellationToken).AsTask();

    ValueTask OnFrameAsync(CameraFrame frame)
    {
        lock (_frameLock)
        {
            _latestFrame = frame; // store reference (immutable payload)
        }
        MainThread.BeginInvokeOnMainThread(_graphicsView.Invalidate);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        CameraFrame frame;
        lock (_frameLock)
            frame = _latestFrame;
        if (frame == null) return;
        if (frame.PixelFormat is not (CameraPixelFormat.Bgra32 or CameraPixelFormat.Rgba32))
        {
            // Unsupported format (e.g., Yuv420). Could add conversion here later.
            return;
        }

        var data = frame.Data;
        int w = frame.Width;
        int h = frame.Height;
        if (w <= 0 || h <= 0) return;
        int step = Math.Clamp(PixelStep, 1, Math.Max(w, h));
        bool bgra = frame.PixelFormat == CameraPixelFormat.Bgra32;
        int stride = 4 * w;
        float scaleX = dirtyRect.Width / w;
        float scaleY = dirtyRect.Height / h;
        for (int y = 0; y < h; y += step)
        {
            int row = y * stride;
            for (int x = 0; x < w; x += step)
            {
                int idx = row + x * 4;
                if (idx + 3 >= data.Length) break;
                byte b = data[idx + (bgra ? 0 : 2)];
                byte g = data[idx + 1];
                byte r = data[idx + (bgra ? 2 : 0)];
                byte a = data[idx + 3];
                canvas.FillColor = Color.FromRgba(r, g, b, a);
                // Draw a rectangle representing this pixel (or block if skipping)
                canvas.FillRectangle(dirtyRect.X + x * scaleX, dirtyRect.Y + y * scaleY, step * scaleX, step * scaleY);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _manager.StopAsync(); } catch { }
        _graphicsView.Drawable = null;
    }
}
