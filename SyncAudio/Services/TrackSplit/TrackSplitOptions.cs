namespace SyncAudio.Services.TrackSplit;

public sealed class TrackSplitOptions
{
    public const string SectionName = "Ffmpeg";

    /// <summary>Path to the ffmpeg binary. Resolved against PATH if just "ffmpeg".</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Path to the ffprobe binary. Resolved against PATH if just "ffprobe".</summary>
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>How long to wait for a single ffmpeg job before giving up.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>Output codec/container: "flac" (lossless, sample-accurate sync) or "mp3" (smaller, lossy).</summary>
    public string OutputFormat { get; set; } = "mp3";

    /// <summary>FLAC compression level 0..8. 5 is the ffmpeg default; 8 is slowest/smallest.</summary>
    public int FlacCompressionLevel { get; set; } = 5;

    /// <summary>MP3 bitrate (e.g. "320k"). Used only when <see cref="OutputFormat"/> is "mp3".</summary>
    public string Mp3Bitrate { get; set; } = "320k";
}
