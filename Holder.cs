using System.Diagnostics;

namespace TimerResTuner;

/// <summary>
/// The background "--hold" mode: a windowless process that requests a timer resolution and
/// keeps it until told to stop. With GlobalTimerResolutionRequests=1 the request applies system-wide.
/// </summary>
internal static class Holder
{
    private const string MutexName = @"Global\TimerResTuner.Holder";
    private const string StopEventName = @"Global\TimerResTuner.Stop";

    public static int Run(uint units)
    {
        // Replace any holder that's already running.
        Stop();

        using var mutex = new Mutex(false, MutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) return 2;
        }
        catch (AbandonedMutexException)
        {
            // Previous holder was killed; we own the mutex now.
        }

        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName);

        Native.KeepTimerResolutionWhenHidden();
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
        Native.NtSetTimerResolution(units, true, out _);

        // Wait for the stop signal. A test build that expires while the PC stays on lets go of the timer
        // (checked every few minutes; the cost is one wake-up per interval).
        while (!stop.WaitOne(TimeSpan.FromMinutes(5)))
        {
            if (BuildInfo.Current.IsExpired(DateTime.UtcNow)) break;
        }

        Native.NtSetTimerResolution(units, false, out _);
        mutex.ReleaseMutex();
        return 0;
    }

    public static bool IsRunning
    {
        get
        {
            if (!Mutex.TryOpenExisting(MutexName, out var m)) return false;
            m.Dispose();
            return true;
        }
    }

    /// <summary>Signals a running holder to exit and waits for it.</summary>
    public static void Stop(int timeoutMs = 3000)
    {
        if (!EventWaitHandle.TryOpenExisting(StopEventName, out var ev)) return;
        using (ev) ev.Set();

        var sw = Stopwatch.StartNew();
        while (IsRunning && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(50);
    }

    public static void Start(uint units)
    {
        if (BuildInfo.Current.IsExpired(DateTime.UtcNow)) return; // expired test builds can't start a holder
        Stop();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--hold {units}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
