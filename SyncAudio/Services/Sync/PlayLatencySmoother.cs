namespace SyncAudio.Services.Sync;

public sealed class PlayLatencySmoother
{
    public const int DefaultMinAcceptedMs = 0;
    public const int DefaultMaxAcceptedMs = 5000;
    public const double DefaultWeight = 0.5;

    private readonly int _minMs;
    private readonly int _maxMs;
    private readonly double _newSampleWeight;
    private bool _hasSample;

    public PlayLatencySmoother(
        int minAcceptedMs = DefaultMinAcceptedMs,
        int maxAcceptedMs = DefaultMaxAcceptedMs,
        double newSampleWeight = DefaultWeight)
    {
        if (maxAcceptedMs < minAcceptedMs)
            throw new ArgumentOutOfRangeException(nameof(maxAcceptedMs));
        if (newSampleWeight is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(newSampleWeight));

        _minMs = minAcceptedMs;
        _maxMs = maxAcceptedMs;
        _newSampleWeight = newSampleWeight;
    }

    public int SmoothedMs { get; private set; }

    public bool TrySubmit(int measuredMs)
    {
        if (measuredMs < _minMs || measuredMs > _maxMs) return false;

        if (!_hasSample)
        {
            SmoothedMs = measuredMs;
            _hasSample = true;
            return true;
        }

        var blended = SmoothedMs * (1 - _newSampleWeight) + measuredMs * _newSampleWeight;
        SmoothedMs = (int)Math.Round(blended, MidpointRounding.AwayFromZero);
        return true;
    }

    public void Reset()
    {
        SmoothedMs = 0;
        _hasSample = false;
    }
}
