using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using SyncAudio.Models;

namespace SyncAudio.Services.TrackSplit;

/// <summary>
/// Server-side audio splitter. For each track in the library, produces two
/// pre-mixed stereo files — a front pair and a back pair — in a single ffmpeg
/// pass via the <c>pan</c> filter. The Now Playing UI streams only ONE of the
/// two to each device so a PC + a mobile together reconstruct a quadro field.
/// Output format (FLAC default, MP3 optional) is configured via
/// <see cref="TrackSplitOptions.OutputFormat"/>; FLAC keeps front and back
/// sample-aligned, which matters for cross-device sync.
///
/// Splitting is on-demand and cached on disk; once a track has been split for
/// a given <see cref="ChannelMapping"/>, no re-work happens unless the cache
/// is wiped or a different mapping is requested.
/// </summary>
public sealed class TrackSplitService(
    IWebHostEnvironment env,
    IOptions<TrackSplitOptions> options,
    ILogger<TrackSplitService> logger)
    : ITrackSplitService
{
    private readonly TrackSplitOptions _opts = options.Value;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<TrackSplitResult> EnsureSplitAsync(
        Track track,
        ChannelMapping? overrideMapping = null,
        string? authHeaderValue = null,
        string? outputFormatOverride = null,
        CancellationToken ct = default)
    {
        var effectiveFormat = string.IsNullOrWhiteSpace(outputFormatOverride)
            ? _opts.OutputFormat
            : outputFormatOverride;

        // Source path is independent of mapping; we need it before we can probe channels.
        var sourceInfo = ResolveSource(track);

        // Auto-pick the mapping (probe layout) if the caller didn't supply one.
        // Probing is cheap relative to ffmpeg, but for local files the result is fully
        // determined by the source — it's safe to skip when an override is supplied
        // (we still report the count/layout we have).
        int channelCount;
        string? channelLayout;
        ChannelMapping mapping;
        if (overrideMapping is not null)
        {
            // We still want channel info in the result so the UI can label dropdowns.
            (channelCount, channelLayout) = await GetChannelInfoAsync(sourceInfo.FfmpegInput, authHeaderValue, ct);
            mapping = overrideMapping;
        }
        else
        {
            (channelCount, channelLayout) = await GetChannelInfoAsync(sourceInfo.FfmpegInput, authHeaderValue, ct);
            mapping = ChannelMapFor(channelCount, channelLayout);
        }

        var paths = ComputePaths(track, mapping, effectiveFormat);

        if (FilesExistAndUpToDate(sourceInfo.LocalPath, paths.FrontPath, paths.BackPath, track.Source))
        {
            return new TrackSplitResult(paths.FrontUrl, paths.BackUrl, channelCount, channelLayout, mapping);
        }

        // Single-flight: only one ffmpeg job per (track, mapping, format) at a time. Different
        // mappings or formats of the same track can run in parallel because their cache files
        // don't collide.
        var lockKey = track.Id + "|" + mapping.ToFileSuffix() + "|" + effectiveFormat;
        var sem = _locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            // Re-check inside the lock — another caller may have just produced them.
            if (FilesExistAndUpToDate(sourceInfo.LocalPath, paths.FrontPath, paths.BackPath, track.Source))
            {
                return new TrackSplitResult(paths.FrontUrl, paths.BackUrl, channelCount, channelLayout, mapping);
            }

            if (track.Source != TrackSource.Plex && !File.Exists(sourceInfo.LocalPath))
            {
                throw new FileNotFoundException($"Source audio file not found: {sourceInfo.LocalPath}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(paths.FrontPath)!);

            logger.LogInformation(
                "Splitting {TrackId} ({Source}, {Channels}ch {Layout}, {Format}) → front=c{FL}|c{FR}, back=c{BL}|c{BR}",
                track.Id, track.Source, channelCount, channelLayout ?? "?", effectiveFormat,
                mapping.FrontL, mapping.FrontR, mapping.BackL, mapping.BackR);

            // Single ffmpeg invocation with two outputs — source is decoded once
            // and the same PCM frames feed both pan filters, guaranteeing that
            // front/back are sample-aligned (critical for cross-device sync).
            await RunSplitFfmpegAsync(
                sourceInfo.FfmpegInput,
                paths.FrontPath, (mapping.FrontL, mapping.FrontR),
                paths.BackPath, (mapping.BackL, mapping.BackR),
                effectiveFormat, authHeaderValue, ct);

            return new TrackSplitResult(paths.FrontUrl, paths.BackUrl, channelCount, channelLayout, mapping);
        }
        finally
        {
            sem.Release();
        }
    }

    private (string FfmpegInput, string LocalPath) ResolveSource(Track track)
    {
        if (track.Source == TrackSource.Plex)
        {
            if (string.IsNullOrEmpty(track.RemoteSourceUrl))
            {
                throw new InvalidOperationException($"Plex track {track.Id} has no RemoteSourceUrl");
            }
            return (track.RemoteSourceUrl, string.Empty);
        }

        var rel = track.AudioUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var local = Path.Combine(env.WebRootPath, rel);
        return (local, local);
    }

    private (string FrontPath, string BackPath, string FrontUrl, string BackUrl)
        ComputePaths(Track track, ChannelMapping mapping, string outputFormat)
    {
        var ext = outputFormat.Equals("mp3", StringComparison.OrdinalIgnoreCase) ? "mp3" : "flac";
        var suffix = mapping.ToFileSuffix();

        string splitDir;
        string frontUrl;
        string backUrl;

        if (track.Source == TrackSource.Plex)
        {
            // Plex: cache by ratingKey under wwwroot/audio/.split/plex/{ratingKey}/
            var ratingKey = track.Id.StartsWith("plex-", StringComparison.Ordinal)
                ? track.Id[5..]
                : track.Id;
            splitDir = Path.Combine(env.WebRootPath, "audio", ".split", "plex", ratingKey);
            var rkEsc = Uri.EscapeDataString(ratingKey);
            frontUrl = $"/audio/.split/plex/{rkEsc}/FL_FR_{suffix}.{ext}";
            backUrl = $"/audio/.split/plex/{rkEsc}/BL_BR_{suffix}.{ext}";
        }
        else
        {
            splitDir = Path.Combine(env.WebRootPath, "audio", ".split", track.Id);
            var idEsc = Uri.EscapeDataString(track.Id);
            frontUrl = $"/audio/.split/{idEsc}/FL_FR_{suffix}.{ext}";
            backUrl = $"/audio/.split/{idEsc}/BL_BR_{suffix}.{ext}";
        }

        var frontPath = Path.Combine(splitDir, $"FL_FR_{suffix}.{ext}");
        var backPath = Path.Combine(splitDir, $"BL_BR_{suffix}.{ext}");
        return (frontPath, backPath, frontUrl, backUrl);
    }

    private static bool FilesExistAndUpToDate(string source, string front, string back, TrackSource sourceKind)
    {
        if (!File.Exists(front) || !File.Exists(back)) return false;
        if (sourceKind == TrackSource.Plex)
        {
            // Plex: ratingKey-keyed cache. If the user replaces the file in Plex
            // its ratingKey may stay the same, but staleness detection over HTTP
            // is expensive; trust the cache until a manual cache wipe.
            return true;
        }
        if (!File.Exists(source)) return true; // source missing, but cache exists — just use it
        var src = File.GetLastWriteTimeUtc(source);
        return File.GetLastWriteTimeUtc(front) >= src
            && File.GetLastWriteTimeUtc(back) >= src;
    }

    /// <summary>
    /// Auto-pick a channel mapping from ffprobe's reported channel count + layout.
    /// Layout-aware so 7.1 (BL/BR at 6/7) doesn't fall back to the 5.1 (BL/BR at 4/5)
    /// rule. When layout is unknown, falls back to the channel-count rule.
    /// </summary>
    public static ChannelMapping ChannelMapFor(int channels, string? layout)
    {
        var normalized = layout?.Trim().ToLowerInvariant();

        if (normalized is not null)
        {
            // 7.1 family: 8 channels, BL/BR live at 6/7. 4/5 are SL/SR (sides).
            if (normalized.StartsWith("7.1"))
                return new ChannelMapping(0, 1, 6, 7);

            // 5.1 family (rear or side): 6 channels, BL/BR (or SL/SR) at 4/5.
            if (normalized.StartsWith("5.1"))
                return new ChannelMapping(0, 1, 4, 5);

            // 5.0 family: 5 channels with rear pair at 3/4.
            if (normalized.StartsWith("5.0"))
                return new ChannelMapping(0, 1, 3, 4);

            // Quadrophonic: 4 channels with rear pair at 2/3.
            if (normalized == "quad" || normalized.StartsWith("quad"))
                return new ChannelMapping(0, 1, 2, 3);

            if (normalized == "stereo")
                return new ChannelMapping(0, 1, 0, 1);

            if (normalized == "mono")
                return new ChannelMapping(0, 0, 0, 0);
        }

        // Fall back to channel-count heuristics for unknown / unrecognised layouts.
        return channels switch
        {
            >= 8 => new ChannelMapping(0, 1, 6, 7),
            >= 6 => new ChannelMapping(0, 1, 4, 5),
            5 => new ChannelMapping(0, 1, 3, 4),
            4 => new ChannelMapping(0, 1, 2, 3),
            3 => new ChannelMapping(0, 1, 0, 1),
            2 => new ChannelMapping(0, 1, 0, 1),
            1 => new ChannelMapping(0, 0, 0, 0),
            _ => new ChannelMapping(0, Math.Min(1, Math.Max(0, channels - 1)), 0, Math.Min(1, Math.Max(0, channels - 1))),
        };
    }

    private async Task RunSplitFfmpegAsync(
        string sourcePath,
        string frontPath, (int l, int r) frontCh,
        string backPath, (int l, int r) backCh,
        string outputFormat,
        string? authHeaderValue, CancellationToken ct)
    {
        var filter =
            $"[0:a:0]pan=stereo|FL=c{frontCh.l}|FR=c{frontCh.r}[front];" +
            $"[0:a:0]pan=stereo|FL=c{backCh.l}|FR=c{backCh.r}[back]";

        var psi = new ProcessStartInfo
        {
            FileName = _opts.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        AppendHttpInputArgs(psi, sourcePath, authHeaderValue);
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-filter_complex"); psi.ArgumentList.Add(filter);

        // Per-output codec flags must appear between the -map and the output path.
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[front]");
        AppendOutputCodecArgs(psi, outputFormat);
        psi.ArgumentList.Add(frontPath);

        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[back]");
        AppendOutputCodecArgs(psi, outputFormat);
        psi.ArgumentList.Add(backPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start ffmpeg ({_opts.FfmpegPath})");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));

        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* race */ }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"ffmpeg exited {proc.ExitCode} for {Path.GetFileName(frontPath)}+{Path.GetFileName(backPath)}: {err.Trim()}");
        }
    }

    private void AppendOutputCodecArgs(ProcessStartInfo psi, string outputFormat)
    {
        if (outputFormat.Equals("mp3", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("libmp3lame");
            psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add(_opts.Mp3Bitrate);
        }
        else
        {
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("flac");
            psi.ArgumentList.Add("-compression_level");
            psi.ArgumentList.Add(_opts.FlacCompressionLevel.ToString());
        }
    }

    private async Task<(int Count, string? Layout)> GetChannelInfoAsync(
        string sourcePath, string? authHeaderValue, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _opts.FfprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        AppendHttpInputArgs(psi, sourcePath, authHeaderValue);
        psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("a:0");
        // key=value output keeps each field on its own labelled line, surviving
        // the case where channel_layout is missing or empty.
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("stream=channels,channel_layout");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=nw=1");
        psi.ArgumentList.Add(sourcePath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start ffprobe ({_opts.FfprobePath})");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        int? channels = null;
        string? layout = null;
        foreach (var rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = rawLine.IndexOf('=');
            if (eq <= 0) continue;
            var key = rawLine[..eq].Trim();
            var value = rawLine[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(value) || value == "unknown" || value == "N/A") continue;

            if (key == "channels" && int.TryParse(value, out var ch) && ch > 0) channels = ch;
            else if (key == "channel_layout") layout = value;
        }

        if (channels is { } c) return (c, layout);

        logger.LogWarning("ffprobe couldn't read channel count from {Path}; defaulting to 2", sourcePath);
        return (2, layout);
    }

    /// <summary>
    /// For HTTP(S) inputs, prepend ffmpeg/ffprobe options that supply auth
    /// headers and reconnect across transient failures. No-op for local files.
    /// Must be called BEFORE the <c>-i</c> argument is added.
    /// </summary>
    private static void AppendHttpInputArgs(ProcessStartInfo psi, string source, string? authHeaderValue)
    {
        if (!IsHttpUrl(source)) return;
        psi.ArgumentList.Add("-reconnect"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_streamed"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_delay_max"); psi.ArgumentList.Add("5");
        if (!string.IsNullOrEmpty(authHeaderValue))
        {
            // ffmpeg -headers takes a single string; CRLF-terminate per RFC.
            psi.ArgumentList.Add("-headers"); psi.ArgumentList.Add(authHeaderValue + "\r\n");
        }
    }

    private static bool IsHttpUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
