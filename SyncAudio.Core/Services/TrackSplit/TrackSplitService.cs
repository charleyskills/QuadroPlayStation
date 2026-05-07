using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using SyncAudio.Core.Models;
using SyncAudio.Core.Services.Storage;

namespace SyncAudio.Core.Services.TrackSplit;

/// <summary>
/// Server-side audio splitter. For each track, produces a front-pair and back-pair
/// stereo file in a single ffmpeg pass via the <c>pan</c> filter so that PC + mobile
/// can play sample-aligned streams for quadro sync.
///
/// Split outputs are stored in the configured object store (MinIO or local FS).
/// Input is resolved as a pre-signed URL (MinIO) or an absolute file path (local),
/// both accepted by ffmpeg. ffmpeg writes to a per-job temp directory; the files
/// are then uploaded to the splits bucket and the temp dir is deleted.
/// </summary>
public sealed class TrackSplitService(
    IObjectStore store,
    IOptions<StorageOptions> storageOpts,
    IOptions<TrackSplitOptions> options,
    ILogger<TrackSplitService> logger,
    IWebHostEnvironment env)
    : ITrackSplitService
{
    private readonly TrackSplitOptions _opts = options.Value;
    private readonly BucketNames _buckets = storageOpts.Value.Buckets;
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

        var ffmpegInput = await ResolveSourceAsync(track, ct);
        var (channelCount, channelLayout) = await GetChannelInfoAsync(ffmpegInput, authHeaderValue, ct);
        var mapping = overrideMapping ?? ChannelMapFor(channelCount, channelLayout);
        var (frontKey, backKey) = ComputeKeys(track, mapping, effectiveFormat);

        if (await SplitExistsAsync(frontKey, backKey, ct))
        {
            TryDeleteLegacyLocalSplit(track);
            return new TrackSplitResult(
                ToClientUrl(frontKey), ToClientUrl(backKey),
                channelCount, channelLayout, mapping);
        }

        var lockKey = track.Id + "|" + mapping.ToFileSuffix() + "|" + effectiveFormat;
        var sem = _locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            if (await SplitExistsAsync(frontKey, backKey, ct))
            {
                TryDeleteLegacyLocalSplit(track);
                return new TrackSplitResult(
                    ToClientUrl(frontKey), ToClientUrl(backKey),
                    channelCount, channelLayout, mapping);
            }

            logger.LogInformation(
                "Splitting {TrackId} ({Source}, {Channels}ch {Layout}, {Format}) → front=c{FL}|c{FR}, back=c{BL}|c{BR}",
                track.Id, track.Source, channelCount, channelLayout ?? "?", effectiveFormat,
                mapping.FrontL, mapping.FrontR, mapping.BackL, mapping.BackR);

            var ext = effectiveFormat.ToLowerInvariant();
            var suffix = mapping.ToFileSuffix();
            var tempDir = Path.Combine(Path.GetTempPath(), "syncaudio-split", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var frontTemp = Path.Combine(tempDir, $"FL_FR_{suffix}.{ext}");
            var backTemp = Path.Combine(tempDir, $"BL_BR_{suffix}.{ext}");

            try
            {
                await RunSplitFfmpegAsync(
                    ffmpegInput,
                    frontTemp, (mapping.FrontL, mapping.FrontR),
                    backTemp, (mapping.BackL, mapping.BackR),
                    effectiveFormat, authHeaderValue, ct);

                var mime = MimeTypeForFormat(ext);
                await using (var fs = File.OpenRead(frontTemp))
                    await store.PutAsync(_buckets.Splits, frontKey, fs, mime, ct);
                await using (var bs = File.OpenRead(backTemp))
                    await store.PutAsync(_buckets.Splits, backKey, bs, mime, ct);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best-effort */ }
            }

            TryDeleteLegacyLocalSplit(track);
            return new TrackSplitResult(
                ToClientUrl(frontKey), ToClientUrl(backKey),
                channelCount, channelLayout, mapping);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<string> ResolveSourceAsync(Track track, CancellationToken ct)
    {
        if (track.Source == TrackSource.Plex)
        {
            if (string.IsNullOrEmpty(track.RemoteSourceUrl))
                throw new InvalidOperationException($"Plex track {track.Id} has no RemoteSourceUrl");
            return track.RemoteSourceUrl;
        }
        // LocalFileObjectStore returns the absolute file path; MinioObjectStore returns a pre-signed URL.
        // Both are accepted by ffmpeg as input sources.
        return await store.GetPresignedGetUrlAsync(
            _buckets.Audio, $"local/{track.Id}.flac", TimeSpan.FromHours(1), ct);
    }

    private (string FrontKey, string BackKey) ComputeKeys(Track track, ChannelMapping mapping, string outputFormat)
    {
        var ext = outputFormat.ToLowerInvariant();
        var suffix = mapping.ToFileSuffix();
        string prefix;

        if (track.Source == TrackSource.Plex)
        {
            var ratingKey = track.Id.StartsWith("plex-", StringComparison.Ordinal) ? track.Id[5..] : track.Id;
            var serverId = track.PlexServerId ?? "default";
            prefix = $"plex/{serverId}/{ratingKey}";
        }
        else
        {
            prefix = $"local/{track.Id}";
        }

        return ($"{prefix}/FL_FR_{suffix}.{ext}", $"{prefix}/BL_BR_{suffix}.{ext}");
    }

    private async Task<bool> SplitExistsAsync(string frontKey, string backKey, CancellationToken ct)
        => await store.ExistsAsync(_buckets.Splits, frontKey, ct)
        && await store.ExistsAsync(_buckets.Splits, backKey, ct);

    // Always tunnels through /storage/ — a MinIO presigned URL embeds the dev-machine
    // MinIO endpoint (localhost:port under Aspire), unreachable from a phone on the LAN.
    private string ToClientUrl(string key)
        => $"/storage/{Uri.EscapeDataString(_buckets.Splits)}/{key}";

    private void TryDeleteLegacyLocalSplit(Track track)
    {
        if (track.Source != TrackSource.Plex || env.WebRootPath is null) return;
        var ratingKey = track.Id.StartsWith("plex-", StringComparison.Ordinal) ? track.Id[5..] : track.Id;
        var dir = Path.Combine(env.WebRootPath, "audio", ".split", "plex", ratingKey);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
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
            if (normalized.StartsWith("7.1"))
                return new ChannelMapping(0, 1, 6, 7);

            if (normalized.StartsWith("5.1"))
                return new ChannelMapping(0, 1, 4, 5);

            if (normalized.StartsWith("5.0"))
                return new ChannelMapping(0, 1, 3, 4);

            if (normalized == "quad" || normalized.StartsWith("quad"))
                return new ChannelMapping(0, 1, 2, 3);

            if (normalized == "stereo")
                return new ChannelMapping(0, 1, 0, 1);

            if (normalized == "mono")
                return new ChannelMapping(0, 0, 0, 0);
        }

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
        else if (outputFormat.StartsWith("opus", StringComparison.OrdinalIgnoreCase))
        {
            // Extract bitrate suffix: "opus256" → "256k". Default to 256k if unrecognised.
            var kbps = outputFormat.Length > 4 ? outputFormat[4..] + "k" : "256k";
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("libopus");
            psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add(kbps);
            psi.ArgumentList.Add("-vbr"); psi.ArgumentList.Add("on");
            psi.ArgumentList.Add("-application"); psi.ArgumentList.Add("audio");
            // .opus256 is not a recognised muxer extension; instruct ffmpeg to use OGG.
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("ogg");
        }
        else
        {
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("flac");
            psi.ArgumentList.Add("-compression_level");
            psi.ArgumentList.Add(_opts.FlacCompressionLevel.ToString());
        }
    }

    private static string MimeTypeForFormat(string ext) => ext switch
    {
        "mp3"  => "audio/mpeg",
        "flac" => "audio/flac",
        _ when ext.StartsWith("opus", StringComparison.OrdinalIgnoreCase) => "audio/ogg",
        _      => "application/octet-stream",
    };

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
            psi.ArgumentList.Add("-headers"); psi.ArgumentList.Add(authHeaderValue + "\r\n");
        }
    }

    private static bool IsHttpUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
