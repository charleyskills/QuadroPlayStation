namespace SyncAudio.Services.Sync;

public readonly record struct ClockSample(long RoundTripMs, double OffsetMs);

public static class ClockSyncFilter
{
    public const int DefaultSampleCount = 12;
    public const int DefaultKeepLowestRttCount = 6;

    public static double Filter(
        IReadOnlyList<ClockSample> samples,
        int keepLowestRtt = DefaultKeepLowestRttCount)
    {
        if (samples.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        if (keepLowestRtt <= 0)
            throw new ArgumentOutOfRangeException(nameof(keepLowestRtt));

        var keep = Math.Min(keepLowestRtt, samples.Count);

        var byRtt = samples.OrderBy(s => s.RoundTripMs).Take(keep);
        var offsetsAscending = byRtt.Select(s => s.OffsetMs).OrderBy(o => o).ToArray();

        return offsetsAscending[offsetsAscending.Length / 2];
    }
}
