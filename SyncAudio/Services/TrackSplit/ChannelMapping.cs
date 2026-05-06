namespace SyncAudio.Services.TrackSplit;

/// <summary>
/// Source-channel indices that ffmpeg's <c>pan</c> filter pulls into the front
/// and back stereo pair files. Used both for auto detection and as a manual
/// override from the UI.
/// </summary>
public sealed record ChannelMapping(int FrontL, int FrontR, int BackL, int BackR)
{
    /// <summary>
    /// Suffix appended to the cached split filenames so different mappings of
    /// the same track coexist on disk.
    /// </summary>
    public string ToFileSuffix() => $"f{FrontL}-{FrontR}_b{BackL}-{BackR}";
}
