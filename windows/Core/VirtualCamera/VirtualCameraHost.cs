using System.Runtime.InteropServices;
using PhoneWebcam.Core.Diagnostics;

namespace PhoneWebcam.Core.VirtualCamera;

/// <summary>
/// Registers the "Phone Webcam" virtual camera for as long as this object lives, by calling the exports of the
/// installed PhoneWebcamSource.dll (scripts/install-vcam.ps1). Session lifetime + current user, so no admin at
/// runtime; Windows removes the camera if the process exits. See docs/virtual-camera.md.
/// </summary>
public sealed unsafe class VirtualCameraHost : IDisposable
{
    public const string FriendlyName = "Phone Webcam";

    private IntPtr _library;
    private delegate* unmanaged[Stdcall]<char*, int> _start;
    private delegate* unmanaged[Stdcall]<int> _stop;

    public static string InstalledDllPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PhoneWebcam", "PhoneWebcamSource.dll");

    public static bool IsInstalled => File.Exists(InstalledDllPath);

    public bool IsRunning { get; private set; }

    /// <summary>Registers and starts the camera. Runs on a thread-pool (MTA) thread, never the UI thread.</summary>
    public Task<int> StartAsync() => Task.Run(() =>
    {
        if (IsRunning) return 0;
        if (!IsInstalled) throw new FileNotFoundException("Virtual camera component is not installed", InstalledDllPath);

        if (_library == IntPtr.Zero)
        {
            _library = NativeLibrary.Load(InstalledDllPath);
            _start = (delegate* unmanaged[Stdcall]<char*, int>)NativeLibrary.GetExport(_library, "PhoneWebcam_StartVirtualCamera");
            _stop = (delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(_library, "PhoneWebcam_StopVirtualCamera");
        }

        int hr;
        fixed (char* name = FriendlyName)
            hr = _start(name);
        if (hr < 0)
        {
            Log.Error($"Virtual camera failed to start: 0x{hr:X8}");
            return hr;
        }
        IsRunning = true;
        Log.Info("Virtual camera started");
        return hr;
    });

    public void Stop()
    {
        if (!IsRunning) return;
        var hr = _stop();
        IsRunning = false;
        Log.Info($"Virtual camera stopped (0x{hr:X8})");
    }

    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch (Exception ex)
        {
            Log.Warn($"Virtual camera stop failed: {ex.Message}");
        }
        // The library stays loaded: unloading a COM server that may still have live objects is unsafe.
    }

    /// <summary>User-facing explanation for a failed start.</summary>
    public static string Explain(int hr) => (uint)hr switch
    {
        0x80070005 => "Windows blocked camera access. Turn on Settings › Privacy & security › Camera › Let desktop apps access your camera.",
        0x80040154 => "The virtual camera component isn't registered. Run scripts\\install-vcam.ps1 as administrator.",
        _ => $"The virtual camera couldn't start (0x{hr:X8}).",
    };
}
