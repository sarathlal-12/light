using System.Runtime.InteropServices;

namespace DotLight;

/// <summary>Reads and sets the PC's master volume (the default speakers/headphones) through Windows Core Audio.</summary>
static class Audio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    /// <summary>Activates a Core Audio interface on the current default output device.</summary>
    static T? Activate<T>() where T : class
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            // 0 = render (speakers/headphones), 1 = multimedia role. Asked fresh so it
            // follows you when you switch output devices.
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0) return null;
            var iid = typeof(T).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o) != 0) return null;
            return (T)o;
        }
        catch { return null; }
    }

    static IAudioEndpointVolume? Endpoint() => Activate<IAudioEndpointVolume>();

    static IAudioMeterInformation? meter;
    static DateTime meterSince;

    /// <summary>
    /// How loud the PC is playing right now, 0–1 (the same reading as the meter in Windows' Sound settings).
    /// Re-attaches every few seconds so it keeps working after you switch to headphones.
    /// </summary>
    public static float Peak()
    {
        if (meter == null || (DateTime.Now - meterSince).TotalSeconds > 4)
        {
            meter = Activate<IAudioMeterInformation>();
            meterSince = DateTime.Now;
        }
        if (meter == null) return 0;
        try { return meter.GetPeakValue(out var p) == 0 ? p : 0; }
        catch { meter = null; return 0; }
    }

    /// <summary>Current volume 0–1 and whether it's muted, or null if there's no output device.</summary>
    public static (double Level, bool Muted)? Get()
    {
        var ep = Endpoint();
        if (ep == null || ep.GetMasterVolumeLevelScalar(out var level) != 0) return null;
        ep.GetMute(out var muted);
        return (level, muted);
    }

    /// <summary>Nudges the volume by delta (e.g. +0.02) and returns the new level; turning up also unmutes.</summary>
    public static (double Level, bool Muted)? Nudge(double delta)
    {
        var ep = Endpoint();
        if (ep == null || ep.GetMasterVolumeLevelScalar(out var level) != 0) return null;
        var ctx = Guid.Empty;
        double next = Math.Clamp(level + delta, 0, 1);
        ep.SetMasterVolumeLevelScalar((float)next, ref ctx);
        ep.GetMute(out var muted);
        if (muted && delta > 0) { ep.SetMute(false, ref ctx); muted = false; }
        return (next, muted || next <= 0.001);
    }
}
