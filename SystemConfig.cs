using System.Diagnostics;
using Microsoft.Win32;

namespace TimerResTuner;

/// <summary>System-level tweaks from the BetterTimerResolution guide.</summary>
internal static class SystemConfig
{
    private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string GlobalValue = "GlobalTimerResolutionRequests";
    public const string TaskName = "TimerResTuner";

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    // GlobalTimerResolutionRequests (Win11 / Server 2022+): makes one process's request apply
    // to every process, like Windows 10 before 2004. Requires a reboot.
    public static bool GlobalRequestsEnabled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(KernelKey);
            return key?.GetValue(GlobalValue) is int v && v == 1;
        }
        set
        {
            using var key = Registry.LocalMachine.CreateSubKey(KernelKey, writable: true);
            if (value) key.SetValue(GlobalValue, 1, RegistryValueKind.DWord);
            else key.DeleteValue(GlobalValue, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// True if this tool (or the tweaks it offers) is currently changing the system: a running holder,
    /// the logon task, GlobalTimerResolutionRequests, or disabled dynamic tick. Used by expired test
    /// builds to decide whether there is still something to undo.
    /// </summary>
    public static bool HasAnythingToUndo() =>
        Holder.IsRunning || StartupTaskInstalled || GlobalRequestsEnabled || DynamicTickDisabled;

    public static bool StartupTaskInstalled => Run("schtasks", $"/Query /TN \"{TaskName}\"").ExitCode == 0;

    public static (int ExitCode, string Output) InstallStartupTask(uint units)
    {
        string tr = $"\\\"{Environment.ProcessPath}\\\" --hold {units}";
        return Run("schtasks", $"/Create /TN \"{TaskName}\" /TR \"{tr}\" /SC ONLOGON /RL HIGHEST /F");
    }

    public static (int ExitCode, string Output) RemoveStartupTask() =>
        Run("schtasks", $"/Delete /TN \"{TaskName}\" /F");

    // bcdedit disabledynamictick: keeps the timer ticking at a fixed rate instead of
    // skipping ticks when idle. Requires a reboot.
    public static bool DynamicTickDisabled =>
        Run("bcdedit", "/enum {current}").Output
            .Split('\n')
            .Any(l => l.TrimStart().StartsWith("disabledynamictick", StringComparison.OrdinalIgnoreCase)
                      && l.Contains("Yes", StringComparison.OrdinalIgnoreCase));

    public static (int ExitCode, string Output) SetDynamicTickDisabled(bool disabled) =>
        disabled
            ? Run("bcdedit", "/set disabledynamictick yes")
            : Run("bcdedit", "/deletevalue disabledynamictick");

    private static (int ExitCode, string Output) Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }
}
