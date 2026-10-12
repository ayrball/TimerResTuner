using System.Globalization;
using System.Reflection;

namespace TimerResTuner;

/// <summary>
/// Optional "test build" stamp baked into the exe at build time (see build-tester.ps1):
/// who the build was made for, and a date after which new changes stop being possible.
/// A normal build has neither and is unrestricted.
///
/// This is a deterrent, not DRM: a determined person can set their clock back. A leaked copy goes
/// dead on its own, and the tester name shows in the title bar and banner.
/// An expired build can still UNDO everything it did (stop the holder, remove the logon task,
/// turn the system tweaks back off), so an expiry never leaves someone's PC tweaked with no way back.
/// </summary>
internal sealed record BuildInfo(string? TesterId, DateTime? ExpiresOn, bool ExpiryInvalid)
{
    private const string DateFormat = "yyyy-MM-dd";
    private const int MaxTesterLength = 40;

    public static BuildInfo Current { get; } = FromAssembly(typeof(BuildInfo).Assembly);

    public bool IsTestBuild => TesterId != null || ExpiresOn != null || ExpiryInvalid;

    public static BuildInfo FromAssembly(Assembly assembly)
    {
        string? Meta(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

        return Parse(Meta("TesterId"), Meta("ExpiresOn"));
    }

    /// <summary>
    /// Parses the stamped values. An expiry that is present but unreadable FAILS CLOSED (treated as
    /// already expired) so a typo can never produce an unlimited build.
    /// </summary>
    public static BuildInfo Parse(string? testerId, string? expiresOn)
    {
        string? tester = Clean(testerId);

        if (string.IsNullOrWhiteSpace(expiresOn))
            return new BuildInfo(tester, null, false);

        if (DateTime.TryParseExact(expiresOn.Trim(), DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return new BuildInfo(tester, date.Date, false);

        return new BuildInfo(tester, null, true);
    }

    private static string? Clean(string? raw)
    {
        var s = new string((raw ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length > MaxTesterLength)
            s = s[..MaxTesterLength].TrimEnd();
        return s.Length == 0 ? null : s;
    }

    // ───────────────────────────────────────────────────────────────

    /// <summary>The build works through the whole of <see cref="ExpiresOn"/> (UTC) and stops at the next midnight.</summary>
    public bool IsExpired(DateTime utcNow) =>
        ExpiryInvalid || (ExpiresOn is { } d && utcNow >= d.AddDays(1));

    /// <summary>Whole days left, counting today (0 = expires tonight). Null if there is no expiry.</summary>
    public int? DaysLeft(DateTime utcNow)
    {
        if (ExpiresOn is not { } d || ExpiryInvalid) return null;
        var remaining = d.AddDays(1) - utcNow;
        return remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalDays) - 1;
    }

    public string ExpiresText => ExpiresOn?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? "";

    /// <summary>One line for the banner.</summary>
    public string Describe(DateTime utcNow)
    {
        string who = TesterId is null ? "Test build" : $"Test build for {TesterId}";
        if (ExpiryInvalid) return $"{who} — expiry could not be read; this build is disabled.";
        if (ExpiresOn is null) return who;
        if (IsExpired(utcNow)) return $"{who} — expired on {ExpiresText}.";

        int left = DaysLeft(utcNow) ?? 0;
        return $"{who} — expires {ExpiresText} ({DaysPhrase(left)}).";
    }

    /// <summary>True when the banner should be drawn as a warning (expired or ≤ 2 days left).</summary>
    public bool NeedsAttention(DateTime utcNow) =>
        IsExpired(utcNow) || (DaysLeft(utcNow) is { } left && left <= 2);

    private static string DaysPhrase(int left) => left switch
    {
        0 => "expires tonight",
        1 => "1 day left",
        _ => $"{left} days left",
    };
}
