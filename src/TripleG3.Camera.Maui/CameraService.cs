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
                // TODO: Implement Android camera enumeration via CameraManager (API 21+) if needed.
                return Task.FromResult<IReadOnlyList<CameraInfo>>(Array.Empty<CameraInfo>());
#elif IOS || MACCATALYST
                // TODO: Implement iOS/MacCatalyst enumeration using AVCaptureDevice.DevicesWithMediaType.
                return Task.FromResult<IReadOnlyList<CameraInfo>>(Array.Empty<CameraInfo>());
#else
                return Task.FromResult<IReadOnlyList<CameraInfo>>(Array.Empty<CameraInfo>());
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
