# TimerResTuner

Benchmarks Windows timer resolutions on your own machine, picks the one with the most precise `Sleep(1)`, and keeps it applied in the background.

It's based on the method from [BetterTimerResolution](https://github.com/plankeeee/BetterTimerResolution) and valleyofdoom's [TimerResolution](https://github.com/valleyofdoom/TimerResolution) (`MeasureSleep` + `micro-adjust-benchmark.ps1`). Unlike those, it is one exe with no PowerShell scripts or execution-policy changes.

## Why not just use 0.5 ms?
At exactly 0.500 ms, `Sleep(1)` often oversleeps by about 0.5 ms or more because of how tick boundaries line up. A value slightly above it (such as 0.507 ms) is usually much more precise. The best value differs from one machine to another, so you have to benchmark it.

## How the benchmark works
For each candidate resolution the app does the following, inside its own process:
1. Calls `NtSetTimerResolution(value)` and waits for it to settle.
2. Times `Sleep(1)` with `QueryPerformanceCounter` N times, with random gaps between calls.
3. Records the mean oversleep (delta), stdev, p99 and max. **Score = mean + stdev** (lower is better).

It runs in three phases:
- **Coarse:** the whole range, in 0.025 ms steps.
- **Fine:** 0.002 ms steps around the 3 best coarse values.
- **Confirm:** re-measures the 5 best values with 150 samples each and picks the winner.

The default settings take about 1–2 minutes. Leave the PC idle while it runs.

**Masked points:** if `GlobalTimerResolutionRequests` is on and another process holds a finer resolution, any coarser request actually runs at that finer one. The app detects this, greys those points out and ignores them. Close other timer tools first for a clean run.

## Applying
- **Apply now:** starts a hidden `TimerResTuner.exe --hold <100ns units>` process that holds the resolution. It opts out of Windows 11's timer throttling for hidden processes.
- **Apply at every logon:** creates a scheduled task `TimerResTuner` that runs at logon with highest privileges. Keep the exe in a fixed location.
- **Stop / restore default:** signals the holder to exit (`TimerResTuner.exe --stop`).
- **GlobalTimerResolutionRequests:** on Windows 11 this is **required** for the holder to affect other processes such as CS2. It takes effect after a reboot.
- **Disable dynamic tick:** runs `bcdedit /set disabledynamictick yes`. This is optional, and its effect is debated. It takes effect after a reboot.

## Build
```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```
Requires .NET 8. The app asks for admin rights for the registry, scheduled task and bcdedit changes.

## Caveats
- A finer timer slightly increases power use and CPU wakeups.
- Validate in-game with CapFrameX or PresentMon (frametime stdev, 1% lows), not only with this benchmark.

## Sending a test copy to someone
```
.\build-tester.ps1 -Tester Alex              # valid 7 days
.\build-tester.ps1 -Tester "Sam K" -Days 3
.\build-tester.ps1 -Tester Jo -ExpiresOn 2026-11-01
```
Each run makes `tester-builds\TimerResTuner-<name>.zip` (the only file to send). It is self-contained, so the tester
does not need .NET installed (`-FrameworkDependent` makes it smaller if they do have the .NET 8 Desktop Runtime).

* The tester's name is in the title bar and a banner at the top of the window.
* After the expiry date (valid through that day, UTC) **benchmarking and applying stop working**, but the app can still
  **undo**: *Stop / restore default*, *Remove from logon*, and turning GlobalTimerResolutionRequests / dynamic tick **off**.
  A running background holder lets go of the timer, and a logon task that fires after expiry removes itself.
  If there is nothing left to undo, the app just shows a message and exits.
* `tester-builds\issued.csv` records who got which build (with its SHA-256) so a leaked copy can be matched.
* An unreadable expiry fails closed (the build is disabled, never unlimited).

This is a deterrent, not DRM: someone determined can set their clock back. Plain `dotnet publish` produces an
unstamped, unrestricted build.
