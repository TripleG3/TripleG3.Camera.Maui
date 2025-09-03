#if ANDROID
using Microsoft.Maui.Handlers;
using Android.Views;
using Android.Graphics;
using Android.Widget;

namespace TripleG3.Camera.Maui;

public sealed class AndroidRemoteVideoViewHandler : ViewHandler<RemoteVideoView, FrameLayout>, IRemoteVideoViewHandler
{
    public static readonly PropertyMapper<RemoteVideoView, AndroidRemoteVideoViewHandler> Mapper = new(ViewHandler.ViewMapper);
    byte[]? _latest;
    int _w, _h;
    readonly object _gate = new();
    TextureView? _texture;
    public AndroidRemoteVideoViewHandler() : base(Mapper) { }
    protected override FrameLayout CreatePlatformView()
    {
        var ctx = Android.App.Application.Context!;
        var container = new FrameLayout(ctx);
        _texture = new TextureView(ctx);
        _texture.SurfaceTextureListener = new Listener(this);
        container.AddView(_texture, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        VirtualView.HandlerImpl = this;
        return container;
    }
    class Listener(AndroidRemoteVideoViewHandler h) : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) => h.Draw();
        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) => true;
        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) => h.Draw();
        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }
    public void OnSizeChanged(double w, double h) => Draw();
    public void UpdateFrame(CameraFrame frame)
    {
        if (frame.Format == CameraPixelFormat.BGRA32)
        {
            lock (_gate) { _latest = frame.Data; _w = frame.Width; _h = frame.Height; }
        }
        else if (frame.Format == CameraPixelFormat.YUV420)
        {
            var rgba = new byte[frame.Width * frame.Height * 4];
            I420ToBGRA(frame.Data, frame.Width, frame.Height, rgba);
            lock (_gate) { _latest = rgba; _w = frame.Width; _h = frame.Height; }
        }
        Draw();
    }
    void Draw()
    {
        var tv = _texture; if (tv == null || !tv.IsAvailable) return;
        byte[]? local; int w, h; lock (_gate) { local = _latest; w = _w; h = _h; }
        var canvas = tv.LockCanvas();
        if (canvas == null) return;
        try
        {
            // Always clear to black background
            canvas.DrawColor(Android.Graphics.Color.Black);
            if (local == null || w == 0 || h == 0) return;
            var bmp = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!);
            try
            {
                var pixels = new int[w * h];
                for (int i = 0, p = 0; i < local.Length; i += 4, p++)
                {
                    byte B = local[i]; byte G = local[i + 1]; byte R = local[i + 2]; byte A = local[i + 3];
                    pixels[p] = (A << 24) | (R << 16) | (G << 8) | B;
                }
                bmp.SetPixels(pixels, 0, w, 0, 0, w, h);
                var vw = tv.Width;
                var vh = tv.Height;
                if (vw > 0 && vh > 0)
                {
                    float scale = Math.Min((float)vw / w, (float)vh / h);
                    int dw = (int)(w * scale);
                    int dh = (int)(h * scale);
                    int dx = (vw - dw) / 2;
                    int dy = (vh - dh) / 2;
                    canvas.DrawBitmap(bmp, null, new Android.Graphics.Rect(dx, dy, dx + dw, dy + dh), null);
                }
                else
                {
                    canvas.DrawBitmap(bmp, 0, 0, null);
                }
            }
            finally { bmp.Dispose(); }
        }
        finally
        {
            tv.UnlockCanvasAndPost(canvas);
        }
    }
    static void I420ToBGRA(byte[] src, int w, int h, byte[] dst)
    {
        int ySize = w * h;
        int uSize = ySize / 4;
        var uOff = ySize; var vOff = ySize + uSize;
        for (int y = 0; y < h; y++)
        {
            int uvRow = (y / 2) * (w / 2);
            for (int x = 0; x < w; x++)
            {
                int yIndex = y * w + x;
                int uvIndex = uvRow + (x / 2);
                byte Y = src[yIndex]; byte U = src[uOff + uvIndex]; byte V = src[vOff + uvIndex];
                int C = Y - 16; if (C < 0) C = 0;
                int D = U - 128; int E = V - 128;
                int R = (298 * C + 409 * E + 128) >> 8;
                int G = (298 * C - 100 * D - 208 * E + 128) >> 8;
                int B = (298 * C + 516 * D + 128) >> 8;
                if (R < 0) R = 0; else if (R > 255) R = 255;
                if (G < 0) G = 0; else if (G > 255) G = 255;
                if (B < 0) B = 0; else if (B > 255) B = 255;
                int di = yIndex * 4;
                dst[di] = (byte)B; dst[di + 1] = (byte)G; dst[di + 2] = (byte)R; dst[di + 3] = 255;
            }
        }
    }
}
#endif
