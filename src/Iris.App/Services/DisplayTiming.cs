using System;
using System.Runtime.InteropServices;

namespace Iris.App.Services;

/// <summary>
/// The refresh period of the display, for the renderer: a frame that WPF composes now is shown about one refresh period later, so the brackets are moved to the expected
/// display time (rendering time plus one period), not to the time of composition. Read from the display settings of the primary display (<c>EnumDisplaySettings</c>,
/// the current mode's frequency); 16.7 ms (60 Hz) when it is not known or not plausible. Cached for a few seconds: the renderer asks every frame.
/// </summary>
internal static class DisplayTiming
{
    public const double FallbackPeriodMs = 1000.0 / 60.0;

    private const long CacheMs = 5000;
    private const int EnumCurrentSettings = -1;

    private static double _periodMs = FallbackPeriodMs;
    private static long _readAtTick = long.MinValue;

    /// <summary>One refresh period in milliseconds (UI thread).</summary>
    public static double RefreshPeriodMs
    {
        get
        {
            var now = Environment.TickCount64;
            if (now - _readAtTick >= CacheMs)
            {
                _readAtTick = now;
                _periodMs = Read();
            }

            return _periodMs;
        }
    }

    /// <summary>The period for a frequency in hertz; the fallback for one that is not plausible (0 and 1 mean "hardware default" to Windows).</summary>
    internal static double PeriodForHz(double hertz) => hertz >= 24 && hertz <= 600 ? 1000.0 / hertz : FallbackPeriodMs;

    private static double Read()
    {
        try
        {
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            return EnumDisplaySettingsW(null, EnumCurrentSettings, ref mode) ? PeriodForHz(mode.DisplayFrequency) : FallbackPeriodMs;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return FallbackPeriodMs;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNumber, ref DevMode mode);

    /// <summary>DEVMODEW (220 bytes), the display part of it.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;          // the union of position / printer fields
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TtOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }
}
