using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PierCam.Camera;

internal enum AsiError
{
    Success = 0, InvalidIndex, InvalidId, InvalidControlType, CameraClosed, CameraRemoved,
    InvalidPath, InvalidFileFormat, InvalidSize, InvalidImgType, OutOfBoundary, Timeout,
    InvalidSequence, BufferTooSmall, VideoModeActive, ExposureInProgress, GeneralError, InvalidMode
}

internal enum AsiImgType { Raw8 = 0, Rgb24 = 1, Raw16 = 2, Y8 = 3, End = -1 }

internal enum AsiBayer { RG = 0, BG = 1, GR = 2, GB = 3 }

internal enum AsiBool { False = 0, True = 1 }

internal enum AsiExposureStatus { Idle = 0, Working = 1, Success = 2, Failed = 3 }

internal enum AsiControlType
{
    Gain = 0, Exposure = 1, Gamma = 2, WbR = 3, WbB = 4, Offset = 5, BandwidthOverload = 6,
    Overclock = 7, Temperature = 8, Flip = 9, AutoMaxGain = 10, AutoMaxExp = 11,
    AutoTargetBrightness = 12, HardwareBin = 13, HighSpeedMode = 14, CoolerPowerPerc = 15,
    TargetTemp = 16, CoolerOn = 17, MonoBin = 18, FanOn = 19, PatternAdjust = 20, AntiDewHeater = 21
}

[StructLayout(LayoutKind.Sequential)]
internal struct AsiCameraInfo
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] NameRaw;
    public int CameraId;
    public int MaxHeight;   // SDK declares `long`, which is 32-bit under MSVC
    public int MaxWidth;
    public AsiBool IsColorCam;
    public AsiBayer BayerPattern;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public int[] SupportedBins;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] SupportedVideoFormat;
    public double PixelSize;
    public AsiBool MechanicalShutter;
    public AsiBool St4Port;
    public AsiBool IsCoolerCam;
    public AsiBool IsUsb3Host;
    public AsiBool IsUsb3Camera;
    public float ElecPerAdu;
    public int BitDepth;
    public AsiBool IsTriggerCam;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Unused;

    public string Name => AsiSdk.FromFixedAscii(NameRaw);
}

[StructLayout(LayoutKind.Sequential)]
internal struct AsiControlCaps
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] NameRaw;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] DescriptionRaw;
    public int MaxValue;
    public int MinValue;
    public int DefaultValue;
    public AsiBool IsAutoSupported;
    public AsiBool IsWritable;
    public AsiControlType ControlType;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Unused;

    public string Name => AsiSdk.FromFixedAscii(NameRaw);
}

[StructLayout(LayoutKind.Sequential)]
internal struct AsiId
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Id;
}

/// <summary>
/// Raw P/Invoke surface for ZWO's ASICamera2.dll.
///
/// The DLL is never copied into the app; <see cref="EnsureLoaded"/> installs a resolver that
/// finds whichever copy the machine already has (ASIStudio, SharpCap, or one dropped next to
/// PierCam.exe). That keeps us working after a ZWO driver update without shipping a stale SDK.
/// </summary>
internal static unsafe class AsiSdk
{
    private const string Lib = "ASICamera2";
    private static bool _resolverInstalled;
    private static string? _resolvedPath;

    public static string? ResolvedPath => _resolvedPath;

    public static void EnsureLoaded()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        NativeLibrary.SetDllImportResolver(typeof(AsiSdk).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (!string.Equals(name, Lib, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;

        foreach (var candidate in CandidatePaths())
        {
            if (!File.Exists(candidate)) continue;
            if (NativeLibrary.TryLoad(candidate, out var h))
            {
                _resolvedPath = candidate;
                return h;
            }
        }

        if (NativeLibrary.TryLoad("ASICamera2.dll", out var fallback))
        {
            _resolvedPath = "ASICamera2.dll (from PATH)";
            return fallback;
        }
        return IntPtr.Zero;
    }

    private static string[] CandidatePaths()
    {
        var appDir = AppContext.BaseDirectory;
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var list = new System.Collections.Generic.List<string>
        {
            Path.Combine(appDir, "ASICamera2.dll"),
            Path.Combine(pf, "ASIStudio", "ASICamera2.dll"),
        };
        // Any installed SharpCap / ZWO folder, newest first.
        foreach (var baseDir in new[] { pf, pf86 })
        {
            if (!Directory.Exists(baseDir)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(baseDir))
                {
                    var leaf = Path.GetFileName(dir);
                    if (leaf.StartsWith("SharpCap", StringComparison.OrdinalIgnoreCase) ||
                        leaf.StartsWith("ZWO", StringComparison.OrdinalIgnoreCase) ||
                        leaf.StartsWith("ASI", StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(Path.Combine(dir, "ASICamera2.dll"));
                    }
                }
            }
            catch (UnauthorizedAccessException) { /* skip unreadable roots */ }
        }
        return list.ToArray();
    }

    public static string FromFixedAscii(byte[]? raw)
    {
        if (raw is null) return string.Empty;
        var len = Array.IndexOf<byte>(raw, 0);
        if (len < 0) len = raw.Length;
        return System.Text.Encoding.ASCII.GetString(raw, 0, len);
    }

    [DllImport(Lib, EntryPoint = "ASIGetNumOfConnectedCameras", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetNumOfConnectedCameras();

    [DllImport(Lib, EntryPoint = "ASIGetCameraProperty", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError GetCameraProperty(out AsiCameraInfo info, int cameraIndex);

    [DllImport(Lib, EntryPoint = "ASIOpenCamera", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError OpenCamera(int cameraId);

    [DllImport(Lib, EntryPoint = "ASIInitCamera", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError InitCamera(int cameraId);

    [DllImport(Lib, EntryPoint = "ASICloseCamera", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError CloseCamera(int cameraId);

    [DllImport(Lib, EntryPoint = "ASIGetNumOfControls", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError GetNumOfControls(int cameraId, out int count);

    [DllImport(Lib, EntryPoint = "ASIGetControlCaps", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError GetControlCaps(int cameraId, int index, out AsiControlCaps caps);

    [DllImport(Lib, EntryPoint = "ASIGetControlValue", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError GetControlValue(int cameraId, AsiControlType type, out int value, out AsiBool auto);

    [DllImport(Lib, EntryPoint = "ASISetControlValue", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError SetControlValue(int cameraId, AsiControlType type, int value, AsiBool auto);

    [DllImport(Lib, EntryPoint = "ASISetROIFormat", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError SetRoiFormat(int cameraId, int width, int height, int bin, AsiImgType imgType);

    [DllImport(Lib, EntryPoint = "ASIGetROIFormat", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError GetRoiFormat(int cameraId, out int width, out int height, out int bin, out AsiImgType imgType);

    [DllImport(Lib, EntryPoint = "ASISetStartPos", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError SetStartPos(int cameraId, int startX, int startY);

    [DllImport(Lib, EntryPoint = "ASIStartVideoCapture", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError StartVideoCapture(int cameraId);

    [DllImport(Lib, EntryPoint = "ASIStopVideoCapture", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError StopVideoCapture(int cameraId);

    [DllImport(Lib, EntryPoint = "ASIGetVideoData", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError GetVideoData(int cameraId, byte* buffer, int bufSize, int waitMs);

    [DllImport(Lib, EntryPoint = "ASIStartExposure", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError StartExposure(int cameraId, AsiBool isDark);

    [DllImport(Lib, EntryPoint = "ASIStopExposure", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError StopExposure(int cameraId);

    [DllImport(Lib, EntryPoint = "ASIGetExpStatus", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError GetExpStatus(int cameraId, out AsiExposureStatus status);

    [DllImport(Lib, EntryPoint = "ASIGetDataAfterExp", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError GetDataAfterExp(int cameraId, byte* buffer, int bufSize);

    [DllImport(Lib, EntryPoint = "ASIGetDroppedFrames", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError GetDroppedFrames(int cameraId, out int dropped);

    [DllImport(Lib, EntryPoint = "ASIGetSerialNumber", CallingConvention = CallingConvention.Cdecl)]
    public static extern AsiError GetSerialNumber(int cameraId, out AsiId sn);

    [DllImport(Lib, EntryPoint = "ASIGetSDKVersion", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetSdkVersionRaw();

    public static string GetSdkVersion()
    {
        try
        {
            var p = GetSdkVersionRaw();
            return p == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(p) ?? "?";
        }
        catch { return "?"; }
    }

    public static string SerialToString(AsiId sn)
    {
        if (sn.Id is null) return string.Empty;
        return string.Concat(Array.ConvertAll(sn.Id, b => b.ToString("X2")));
    }
}
