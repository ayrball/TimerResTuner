using System.Diagnostics;

namespace TimerResTuner;

/// <summary>One measured resolution. Deltas are how much longer Sleep(1) took than 1 ms.</summary>
internal sealed record BenchPoint(
    string Phase,
    uint RequestedUnits,
    double ActualMs,
    int Samples,
    double MeanDelta,
    double StdDev,
    double P99Delta,
    double MaxDelta)
{
    public double RequestedMs => RequestedUnits / 10000.0;

    /// <summary>
    /// Another process held a finer resolution during this measurement (global timer mode),
    /// so the result reflects that process's resolution, not the one we requested.
    /// </summary>
    public bool Masked => ActualMs < RequestedMs - 0.001;

    /// <summary>Lower is better: rewards both low average oversleep and low jitter.</summary>
    public double Score => MeanDelta + StdDev;
}

internal sealed class BenchmarkSettings
{
    public double StartMs { get; init; } = 0.500;
    public double EndMs { get; init; } = 1.000;
    public double CoarseStepMs { get; init; } = 0.025;
    public double FineStepMs { get; init; } = 0.002;
    public int Samples { get; init; } = 40;
    public int ConfirmSamples { get; init; } = 150;
    public int RefineAround { get; init; } = 3;
    public int Finalists { get; init; } = 5;
    public int SettleMs { get; init; } = 300;
}

/// <summary>
/// Sweeps timer resolutions in-process and measures Sleep(1) precision at each one,
/// the same method as valleyofdoom's MeasureSleep / micro-adjust-benchmark, in three phases:
///   1. coarse sweep across the whole range,
///   2. fine sweep around the best coarse results,
///   3. re-measure the finalists with many more samples and pick the lowest score.
/// </summary>
internal sealed class Benchmarker
{
    public event Action<BenchPoint>? PointMeasured;
    public event Action<int, int, string>? Progress;

    private readonly Random _rng = new();
    private int _done;
    private int _total;

    public BenchPoint Run(BenchmarkSettings s, CancellationToken ct)
    {
        var info = TimerInfo.Query();
        uint finest = (uint)Math.Round(info.FinestMs * 10000);
        uint start = Math.Max(ToUnits(s.StartMs), finest);
        uint end = Math.Max(ToUnits(s.EndMs), start);
        uint coarseStep = Math.Max(1u, ToUnits(s.CoarseStepMs));
        uint fineStep = Math.Max(1u, ToUnits(s.FineStepMs));

        var measured = new Dictionary<uint, BenchPoint>();

        // Phase 1: coarse
        var coarse = new List<uint>();
        for (uint u = start; u <= end; u += coarseStep) coarse.Add(u);
        if (coarse[^1] != end) coarse.Add(end);

        int finePerCandidate = (int)(2 * coarseStep / fineStep);
        _total = coarse.Count + s.RefineAround * finePerCandidate + s.Finalists;

        foreach (var u in coarse)
            measured[u] = Measure("coarse", u, s.Samples, s.SettleMs, ct);

        // Phase 2: fine sweep around the best coarse points
        var fine = new SortedSet<uint>();
        foreach (var c in Rank(measured.Values).Take(s.RefineAround))
        {
            uint lo = c.RequestedUnits > start + coarseStep ? c.RequestedUnits - coarseStep : start;
            uint hi = Math.Min(c.RequestedUnits + coarseStep, end);
            for (uint u = lo; u <= hi; u += fineStep)
                if (!measured.ContainsKey(u)) fine.Add(u);
        }
        _total = coarse.Count + fine.Count + s.Finalists;

        foreach (var u in fine)
            measured[u] = Measure("fine", u, s.Samples, s.SettleMs, ct);

        // Phase 3: confirm the finalists with many more samples to beat the noise
        var finalists = Rank(measured.Values).Take(s.Finalists).Select(p => p.RequestedUnits).ToList();
        _total = coarse.Count + fine.Count + finalists.Count;

        var confirmed = finalists.Select(u => Measure("confirm", u, s.ConfirmSamples, s.SettleMs, ct)).ToList();
        return Rank(confirmed).First();
    }

    /// <summary>Best first; masked points only count if nothing else was measurable.</summary>
    private static IEnumerable<BenchPoint> Rank(IEnumerable<BenchPoint> points) =>
        points.OrderBy(p => p.Masked).ThenBy(p => p.Score);

    private BenchPoint Measure(string phase, uint units, int samples, int settleMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Progress?.Invoke(_done, _total, $"{phase}: {units / 10000.0:0.0000} ms");

        Native.NtSetTimerResolution(units, true, out _);
        try
        {
            // Results are unreliable if we measure immediately after changing the resolution.
            Native.Sleep((uint)settleMs);
            Native.NtQueryTimerResolution(out _, out _, out uint current);

            // Discard the first call, it starts mid-tick.
            Native.Sleep(1);

            double tickToMs = 1000.0 / Stopwatch.Frequency;
            var deltas = new double[samples];
            for (int i = 0; i < samples; i++)
            {
                ct.ThrowIfCancellationRequested();
                long t0 = Stopwatch.GetTimestamp();
                Native.Sleep(1);
                long t1 = Stopwatch.GetTimestamp();
                deltas[i] = (t1 - t0) * tickToMs - 1.0;

                // Random gap so samples start at different phases of the timer tick.
                Native.Sleep((uint)_rng.Next(5, 16));
            }

            Array.Sort(deltas);
            double mean = deltas.Average();
            double variance = deltas.Sum(d => (d - mean) * (d - mean)) / Math.Max(1, samples - 1);
            int p99Index = Math.Clamp((int)Math.Ceiling(samples * 0.99) - 1, 0, samples - 1);

            var point = new BenchPoint(phase, units, current / 10000.0, samples,
                mean, Math.Sqrt(variance), deltas[p99Index], deltas[^1]);

            _done++;
            PointMeasured?.Invoke(point);
            Progress?.Invoke(_done, _total, $"{phase}: {units / 10000.0:0.0000} ms");
            return point;
        }
        finally
        {
            Native.NtSetTimerResolution(units, false, out _);
        }
    }

    private static uint ToUnits(double ms) => (uint)Math.Round(ms * 10000);
}
