namespace SyncAudio.Services.TrackSplit;

/// <summary>
/// URLs of the two pre-split stereo streams for a track plus the source-channel
/// info that produced them. The UI uses the channel info to populate the
/// "Advanced" pair selector. Each URL is served as a static file under
/// wwwroot/audio/.split/&lt;trackId&gt;/.
/// </summary>
public sealed record TrackSplitResult(
    string FrontUrl,
    string BackUrl,
    int SourceChannelCount,
    string? SourceChannelLayout,
    ChannelMapping EffectiveMapping);
