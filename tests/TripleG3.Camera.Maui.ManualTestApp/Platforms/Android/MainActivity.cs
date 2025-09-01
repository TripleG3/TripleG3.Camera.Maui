using Android.App;
using Android.Content.PM;
using Android.OS;
using Android;

namespace TripleG3.Camera.Maui.ManualTestApp
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            RequestCameraPermissionIfNeeded();
        }

        const int RequestId = 42;
        void RequestCameraPermissionIfNeeded()
        {
            if ((int)Build.VERSION.SdkInt < 23) return; // Permissions auto-granted pre-Marshmallow
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
            {
#pragma warning disable CA1422
                if (CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted) return;
                RequestPermissions(new[] { Manifest.Permission.Camera }, RequestId);
#pragma warning restore CA1422
            }
        }
    }
}
