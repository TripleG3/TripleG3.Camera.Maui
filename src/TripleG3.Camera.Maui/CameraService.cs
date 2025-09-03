using System.Collections.Concurrent;

namespace TripleG3.Camera.Maui;

/// <summary>
/// Cross-platform camera enumeration service.
/// Only Windows currently implemented; other platforms return empty list (placeholder).
/// </summary>
public sealed class CameraService : ICameraService
{   
        readonly ConcurrentDictionary<string, CameraInfo> _cache = new();

        public Task<IReadOnlyList<CameraInfo>> GetCamerasAsync()
        {
#if WINDOWS
                return GetWindowsCamerasAsync();
#elif ANDROID
                try
                {
                        var mgr = Android.App.Application.Context.GetSystemService(Android.Content.Context.CameraService) as Android.Hardware.Camera2.CameraManager;
                        if (mgr == null) return Task.FromResult<IReadOnlyList<CameraInfo>>([]);
                        var ids = mgr.GetCameraIdList();
                        var list = new List<CameraInfo>(ids.Length);
                        foreach (var id in ids)
                        {
                                try
                                {
                                        var chars = mgr.GetCameraCharacteristics(id);
                                        var lens = chars.Get(Android.Hardware.Camera2.CameraCharacteristics.LensFacing) as Java.Lang.Integer;
                                        var facing = lens == null ? -1 : lens.IntValue();
                                        string name;
                                        if (facing == -1)
                                                name = id;
                                        else if (facing == (int)Android.Hardware.Camera2.LensFacing.Front)
                                                name = $"Front ({id})";
                                        else
                                                name = $"Back ({id})";
                                        var info = _cache.GetOrAdd(id, _ => new CameraInfo(id, name));
                                        list.Add(info);
                                }
                                catch { }
                        }
                        return Task.FromResult<IReadOnlyList<CameraInfo>>(list);
                }
                catch { return Task.FromResult<IReadOnlyList<CameraInfo>>([]); }
#elif IOS || MACCATALYST
                // TODO: Implement iOS/MacCatalyst enumeration using AVCaptureDevice.DevicesWithMediaType.
                return Task.FromResult<IReadOnlyList<CameraInfo>>([]);
#else
                return Task.FromResult<IReadOnlyList<CameraInfo>>([]);
#endif
        }

#if WINDOWS
        async Task<IReadOnlyList<CameraInfo>> GetWindowsCamerasAsync()
        {
                var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture);
                var list = new List<CameraInfo>(devices.Count);
                foreach (var d in devices)
                {
                        var info = _cache.GetOrAdd(d.Id, id => new CameraInfo(id, d.Name));
                        list.Add(info);
                }
                return list;
        }
#endif
}
