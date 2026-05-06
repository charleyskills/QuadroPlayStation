namespace SyncAudio.Services.Sync;

/// <summary>
/// One member's reported buffer progress, anchored on the FIRST sample we saw for the
/// current decode. Linear extrapolation from (FirstFraction, FirstTimestampMs) to
/// (LastFraction, LastTimestampMs) gives a download-rate estimate.
///
/// A drop in fraction (e.g. user switched track / setSrc) re-anchors First* to the new
/// sample — see SyncHub.ReportProgress.
/// </summary>
public readonly record struct ProgressSample(
    double FirstFraction,
    long FirstTimestampMs,
    double LastFraction,
    long LastTimestampMs)
{
    public static ProgressSample Empty(long nowMs) => new(0.0, nowMs, 0.0, nowMs);
}

public readonly record struct AdaptiveLeadResult(
    long LeadMs,
    long SlowestRemainingMs,
    double SlowestFraction,
    bool CapHit);

/// <summary>
/// Pure function: given each group member's last reported buffer-progress sample,
/// compute a lead time for the next ScheduledPlay broadcast such that the slowest
/// peer is expected to finish decoding before the play moment.
///
/// Why this exists: <see cref="Hubs.SyncHub.RequestPlay"/> previously broadcast a
/// fixed lead, so any peer not 100% ready by `broadcast + lead` fell into the JS
/// recurse-and-clamp path (syncAudio.js:342-372) and audibly drifted. This adapts
/// the lead so all peers finish in time, capped at <see cref="MaxLeadMs"/>; if the
/// cap is hit, sync may still drift on the slow peer and the caller is expected to
/// surface a "waiting for peers" advisory.
/// </summary>
public static class AdaptiveLeadCalculator
{
    public const long MinLeadMs = 5_000;
    public const long MaxLeadMs = 30_000;
    public const long DecodeBufferMs = 1_000;

    public static AdaptiveLeadResult Compute(
        IReadOnlyList<ProgressSample> memberSamples,
        long nowMs,
        double fallbackLeadSeconds)
    {
        var fallbackMs = (long)(fallbackLeadSeconds * 1000);
        var floor = Math.Max(MinLeadMs, fallbackMs);

        if (memberSamples.Count == 0)
            return new AdaptiveLeadResult(floor, 0, 1.0, CapHit: false);

        long slowestRemainingMs = 0;
        double slowestFraction = 1.0;

        foreach (var s in memberSamples)
        {
            var remaining = EstimateRemainingMs(s, nowMs);
            if (remaining > slowestRemainingMs)
            {
                slowestRemainingMs = remaining;
                slowestFraction = s.LastFraction;
            }
        }

        var desired = slowestRemainingMs + DecodeBufferMs;
        var capHit = desired > MaxLeadMs;
        var lead = Math.Max(floor, Math.Min(MaxLeadMs, desired));
        return new AdaptiveLeadResult(lead, slowestRemainingMs, slowestFraction, capHit);
    }

    private static long EstimateRemainingMs(ProgressSample s, long nowMs)
    {
        if (s.LastFraction >= 1.0) return 0;

        var elapsedSinceFirst = nowMs - s.FirstTimestampMs;
        var delta = s.LastFraction - s.FirstFraction;

        // No useful sample yet (just joined, hasn't reported, or stuck at 0):
        // assume the worst — return MaxLeadMs so the cap path triggers.
        if (elapsedSinceFirst <= 0 || delta <= 0) return MaxLeadMs;

        var fractionPerMs = delta / elapsedSinceFirst;
        var remaining = (1.0 - s.LastFraction) / fractionPerMs;
        return (long)Math.Ceiling(remaining);
    }
}
