#if WINDOWS
using Microsoft.Maui.Handlers;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace TripleG3.Camera.Maui;

public sealed class WindowsRemoteVideoViewHandler : ViewHandler<RemoteVideoView, CanvasControl>, IRemoteVideoViewHandler
{
    public static readonly PropertyMapper<RemoteVideoView, WindowsRemoteVideoViewHandler> Mapper = new(ViewHandler.ViewMapper);
    byte[]? _latest;
    int _w, _h;
    readonly object _gate = new();
    CanvasControl? _canvas;
    public WindowsRemoteVideoViewHandler() : base(Mapper) { }
    protected override CanvasControl CreatePlatformView()
    {
        _canvas = new CanvasControl();
        _canvas.Draw += OnDraw;
        VirtualView.HandlerImpl = this;
        return _canvas;
    }

    void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        byte[]? local; int w, h;
        lock (_gate) { local = _latest; w = _w; h = _h; }
        if (local == null) return;
        if (local.Length != w * h * 4) return; // expect BGRA32
        using var bmp = CanvasBitmap.CreateFromBytes(sender, local, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        args.DrawingSession.DrawImage(bmp);
    }

    public void OnSizeChanged(double w, double h) => _canvas?.Invalidate();

    public void UpdateFrame(CameraFrame frame)
    {
        if (frame.Format == CameraPixelFormat.BGRA32)
        {
            lock (_gate)
            {
                _latest = frame.Data;
                _w = frame.Width;
                _h = frame.Height;
            }
            _canvas?.Invalidate();
        }
        else if (frame.Format == CameraPixelFormat.YUV420)
        {
            // Convert I420 -> BGRA simple (slow) conversion for prototype
            var rgba = new byte[frame.Width * frame.Height * 4];
            I420ToBGRA(frame.Data, frame.Width, frame.Height, rgba);
            lock (_gate)
            {
                _latest = rgba;
                _w = frame.Width; _h = frame.Height;
            }
            _canvas?.Invalidate();
        }
    }

    static void I420ToBGRA(byte[] src, int w, int h, byte[] dst)
    {
        int ySize = w * h;
        int uSize = ySize / 4;
        var uOff = ySize;
        var vOff = ySize + uSize;
        for (int y = 0; y < h; y++)
        {
            int uvRow = (y / 2) * (w / 2);
            for (int x = 0; x < w; x++)
            {
                int yIndex = y * w + x;
                int uvIndex = uvRow + (x / 2);
                byte Y = src[yIndex];
                byte U = src[uOff + uvIndex];
                byte V = src[vOff + uvIndex];
                int C = Y - 16; if (C < 0) C = 0;
                int D = U - 128;
                int E = V - 128;
                int R = (298 * C + 409 * E + 128) >> 8;
                int G = (298 * C - 100 * D - 208 * E + 128) >> 8;
                int B = (298 * C + 516 * D + 128) >> 8;
                if (R < 0) R = 0; else if (R > 255) R = 255;
                if (G < 0) G = 0; else if (G > 255) G = 255;
                if (B < 0) B = 0; else if (B > 255) B = 255;
                int di = (yIndex) * 4;
                dst[di + 0] = (byte)B;
                dst[di + 1] = (byte)G;
                dst[di + 2] = (byte)R;
                dst[di + 3] = 255;
            }
        }
    }
}
#endif
