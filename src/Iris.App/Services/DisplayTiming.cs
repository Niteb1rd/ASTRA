using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Iris.App.Services;

/// <summary>
/// The refresh period of the display, for the renderer: a frame that WPF composes now is shown about one refresh period later, so the brackets are moved to the expected
/// display time (rendering time plus one period), not to the time of composition. The period is the one of the monitor the GAME is on (not the primary display):
/// <c>MonitorFromWindow(game, MONITOR_DEFAULTTONEAREST)</c>, <c>GetMonitorInfoW</c> (MONITORINFOEX: the device name), <c>EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS)</c>, the
/// mode's <c>dmDisplayFrequency</c>. Cached per monitor for 5 seconds (the renderer asks every frame); 16.7 ms (60 Hz) when it is not known or not plausible.
/// </summary>
internal static class DisplayTiming
{
    public const double FallbackPeriodMs = 1000.0 / 60;

    private const long CacheMs = 5000;
    private const int EnumCurrentSettings = -1; // ENUM_CURRENT_SETTINGS is ((DWORD)-1)
    private const int DevModeSize = 220;        // sizeof(DEVMODEW)
    private const int MaxCachedMonitors = 16;

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, (long AtTick, double PeriodMs)> Cache = new(); // monitor handle -> when it was read, the period; guarded by Gate

    /// <summary>
    /// One refresh period in milliseconds of the monitor that holds <paramref name="gameWindow"/> (its nearest monitor when it is between two; the primary monitor when the handle is
    /// zero, for example while no game window is known).
    /// </summary>
    public static double RefreshPeriodMs(IntPtr gameWindow)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromWindow(
                gameWindow, gameWindow == IntPtr.Zero ? NativeMethods.MonitorDefaultToPrimary : NativeMethods.MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
                return FallbackPeriodMs;

            var now = Environment.TickCount64;
            lock (Gate)
            {
                if (Cache.TryGetValue(monitor, out var known) && now - known.AtTick < CacheMs)
                    return known.PeriodMs;
            }

            var period = Read(monitor);
            lock (Gate)
            {
                if (Cache.Count >= MaxCachedMonitors)
                    Cache.Clear(); // monitors come and go (docking, remote sessions); do not collect their handles forever
                Cache[monitor] = (now, period);
            }

            return period;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return FallbackPeriodMs;
        }
    }

    /// <summary>The period for a frequency in hertz; the fallback for one that is not plausible (0 and 1 mean "hardware default" to Windows).</summary>
    internal static double PeriodForHz(double hertz) => hertz >= 24 && hertz <= 600 ? 1000.0 / hertz : FallbackPeriodMs;

    private static double Read(IntPtr monitor)
    {
        if (Marshal.SizeOf<DevMode>() != DevModeSize)
            return FallbackPeriodMs; // the structure is not what Windows expects: better no answer than a wrong one

        var info = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>() };
        if (!NativeMethods.GetMonitorInfoEx(monitor, ref info) || string.IsNullOrEmpty(info.Device))
            return FallbackPeriodMs;

        var mode = new DevMode { Size = (ushort)DevModeSize };
        return EnumDisplaySettingsW(info.Device, EnumCurrentSettings, ref mode) ? PeriodForHz(mode.DisplayFrequency) : FallbackPeriodMs;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNumber, ref DevMode mode);

    /// <summary>
    /// DEVMODEW, 220 bytes. Offsets: device name 0 (64 bytes), SpecVersion 64, DriverVersion 66, Size 68, DriverExtra 70, Fields 72, the 16 byte union of the printer fields and the display
    /// fields 76 (position x and y, display orientation, fixed output), Color 92 ... Collate 100, form name 102 (64 bytes), LogPixels 166, BitsPerPel 168, PelsWidth 172, PelsHeight 176,
    /// DisplayFlags 180, DisplayFrequency 184, then five more DWORDs to the end at 220. Sequential layout needs no padding anywhere (every field sits on its own alignment), which
    /// <see cref="Read"/> checks against <see cref="DevModeSize"/>.
    /// </summary>
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
        public int PositionX;          // the union of position / printer fields (16 bytes: x, y, orientation, fixed output)
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
