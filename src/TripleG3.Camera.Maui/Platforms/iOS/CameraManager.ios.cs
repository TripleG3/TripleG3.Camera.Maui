#if IOS
using AVFoundation;
using CoreMedia;
using CoreVideo;
using Foundation;

namespace TripleG3.Camera.Maui;

internal sealed class IosCameraManager : CameraManager
{
    AVCaptureSession? _session;
    AVCaptureDeviceInput? _input;
    AVCaptureVideoDataOutput? _output;

    const string MediaTypeVideo = "vide"; // AVMediaType.Video constant value

    static AVCaptureDevice[] GetVideoDevices() => AVCaptureDevice.DevicesWithMediaType(MediaTypeVideo);

    public override ValueTask<IReadOnlyList<CameraInfo>> GetCamerasAsync(CancellationToken cancellationToken = default)
    {
        var devices = GetVideoDevices();
        var list = devices.Select(d => new CameraInfo(
            d.UniqueID,
            d.LocalizedName,
            d.Position switch
            {
                AVCaptureDevicePosition.Front => CameraFacing.Front,
                AVCaptureDevicePosition.Back => CameraFacing.Back,
                _ => CameraFacing.Unknown
            },
            false
        )).ToList();
        return ValueTask.FromResult<IReadOnlyList<CameraInfo>>(list);
    }

    public override async ValueTask SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            if (SelectedCamera?.Id == cameraId) return;
            var cams = await GetCamerasAsync(cancellationToken);
            SelectedCamera = cams.FirstOrDefault(c => c.Id == cameraId) ?? throw new ArgumentException("Camera not found", nameof(cameraId));
            if (IsStreaming)
                await StopInternalAsync();
        }
        finally { SyncLock.Release(); }
    }

    public override async ValueTask StartAsync(Func<CameraFrame, ValueTask> frameCallback, CancellationToken cancellationToken = default)
    {
        if (IsStreaming) return;
        if (SelectedCamera is null) throw new InvalidOperationException("Select a camera first.");
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            if (IsStreaming) return;
            FrameCallback = frameCallback;
            _session = new AVCaptureSession
            {
                SessionPreset = AVCaptureSession.Preset1280x720
            };
            var device = GetVideoDevices().First(d => d.UniqueID == SelectedCamera.Id);
            _input = new AVCaptureDeviceInput(device, out var err);
            if (err is not null) throw new NSErrorException(err);
            if (_session.CanAddInput(_input)) _session.AddInput(_input);
            _output = new AVCaptureVideoDataOutput
            {
                AlwaysDiscardsLateVideoFrames = true
            };
            _output.SetSampleBufferDelegate(new SampleDelegate(this), CoreFoundation.DispatchQueue.MainQueue);
            if (_session.CanAddOutput(_output)) _session.AddOutput(_output);
            _session.StartRunning();
            IsStreaming = true;
        }
        finally { SyncLock.Release(); }
    }

    public override async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await SyncLock.WaitAsync(cancellationToken);
        try { await StopInternalAsync(); }
        finally { SyncLock.Release(); }
    }

    Task StopInternalAsync()
    {
        IsStreaming = false;
        _session?.StopRunning();
        _output?.Dispose(); _output = null;
        _input?.Dispose(); _input = null;
        _session?.Dispose(); _session = null;
        return Task.CompletedTask;
    }

    sealed class SampleDelegate(IosCameraManager owner) : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        public override void DidOutputSampleBuffer(AVCaptureOutput captureOutput, CMSampleBuffer sampleBuffer, AVCaptureConnection connection)
        {
            if (!owner.IsStreaming) return;
            using var pb = sampleBuffer.GetImageBuffer() as CVPixelBuffer;
            if (pb == null) return;
            pb.Lock(CVPixelBufferLock.ReadOnly);
            try
            {
                var width = (int)pb.Width;
                var height = (int)pb.Height;
                var bytesPerRow = (int)pb.BytesPerRow;
                var length = bytesPerRow * height;
                var data = new byte[length];
                System.Runtime.InteropServices.Marshal.Copy(pb.BaseAddress, data, 0, length);
                var frame = new CameraFrame
                {
                    CameraId = owner.SelectedCamera!.Id,
                    TimestampUtcTicks = DateTime.UtcNow.Ticks,
                    Width = width,
                    Height = height,
                    PixelFormat = CameraPixelFormat.Bgra32,
                    Data = data
                };
                _ = owner.OnFrameAsync(frame);
            }
            finally
            {
                pb.Unlock(CVPixelBufferLock.ReadOnly);
            }
        }
    }
}
#endif
