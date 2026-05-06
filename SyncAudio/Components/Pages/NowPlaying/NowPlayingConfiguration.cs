namespace SyncAudio.Components.Pages.NowPlaying;

public sealed record NowPlayingConfiguration
{
    public static NowPlayingConfiguration Default => new();

    public int GlassIntensity { get; init; } = 60;
    public TimeSpan FadeAnimationTimeout { get; init; } = TimeSpan.FromMilliseconds(420);
    public double LeadSeconds { get; init; } = 3.0;
    public double DefaultVolume { get; init; } = 0.75;
}
