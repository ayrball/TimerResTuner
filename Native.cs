using System.Runtime.InteropServices;

namespace TimerResTuner;

/// <summary>
/// Undocumented ntdll timer APIs. All resolution values are in 100-nanosecond units
/// (5000 = 0.5 ms, 10000 = 1 ms, 156250 = 15.625 ms).
/// </summary>
internal static class Native
{
    [DllImport("ntdll.dll")]
    public static extern int NtSetTimerResolution(
        uint desiredResolution,
        [MarshalAs(UnmanagedType.U1)] bool setResolution,
        out uint currentResolution);

    // Note the naming: "Minimum" is the coarsest interval (largest number), "Maximum" the finest.
    [DllImport("ntdll.dll")]
    public static extern int NtQueryTimerResolution(
        out uint minimumResolution,
        out uint maximumResolution,
        out uint currentResolution);

    [DllImport("kernel32.dll")]
    public static extern void Sleep(uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess, int processInformationClass,
        ref ProcessPowerThrottlingState processInformation, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const int ProcessPowerThrottling = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;

    /// <summary>
    /// Windows 11 may ignore timer resolution requests from processes without a visible window.
    /// Taking control of the IGNORE_TIMER_RESOLUTION bit with state 0 opts this process out of that.
    /// </summary>
    public static bool KeepTimerResolutionWhenHidden()
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = ProcessPowerThrottlingIgnoreTimerResolution,
            StateMask = 0,
        };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
            (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
    }
}

internal readonly record struct TimerInfo(double CoarsestMs, double FinestMs, double CurrentMs)
{
    public static TimerInfo Query()
    {
        Native.NtQueryTimerResolution(out var min, out var max, out var cur);
        return new TimerInfo(min / 10000.0, max / 10000.0, cur / 10000.0);
    }
}
