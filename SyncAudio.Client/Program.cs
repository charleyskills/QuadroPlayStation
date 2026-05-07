using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Minio;
using SyncAudio.Client.Components;
using SyncAudio.Client.Components.Pages.NowPlaying;
using SyncAudio.Core.Hubs;
using SyncAudio.Core.Services;
using SyncAudio.Core.Services.CoverArt;
using SyncAudio.Core.Services.Plex;
using SyncAudio.Core.Services.Storage;
using SyncAudio.Core.Services.Sync.StateMachines;
using SyncAudio.Core.Services.TrackSplit;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

// Aspire injects ConnectionStrings__minio when running under the AppHost.
// Manual Storage:Provider=Minio is used for docker-compose / standalone runs.
var aspireMinioCs = builder.Configuration.GetConnectionString("minio");
var storageProvider = builder.Configuration[$"{StorageOptions.SectionName}:Provider"] ?? "Local";
var useMinio = !string.IsNullOrEmpty(aspireMinioCs)
               || storageProvider.Equals("Minio", StringComparison.OrdinalIgnoreCase);

if (!string.IsNullOrEmpty(aspireMinioCs))
{
    builder.AddMinioClient("minio");
    builder.Services.AddSingleton<IObjectStore, MinioObjectStore>();
}
else if (storageProvider.Equals("Minio", StringComparison.OrdinalIgnoreCase))
{
    var minioSection = builder.Configuration.GetSection($"{StorageOptions.SectionName}:Minio");
    builder.Services.AddSingleton<IMinioClient>(_ =>
        new MinioClient()
            .WithEndpoint(minioSection["Endpoint"] ?? "localhost:9000")
            .WithCredentials(minioSection["AccessKey"] ?? "", minioSection["SecretKey"] ?? "")
            .WithSSL(bool.TryParse(minioSection["UseSsl"], out var ssl) && ssl)
            .Build());
    builder.Services.AddSingleton<IObjectStore, MinioObjectStore>();
}
else
{
    builder.Services.AddSingleton<IObjectStore, LocalFileObjectStore>();
}
builder.Services.AddSingleton<ICoverArtService, CoverArtService>();
builder.Services.AddSingleton<ITrackLibraryService, TrackLibraryService>();
builder.Services.AddSingleton<IPlaybackOrchestrator, PlaybackOrchestrator>();
builder.Services.AddScoped<INowPlayingContextFactory, NowPlayingContextFactory>();

builder.Services.Configure<TrackSplitOptions>(
    builder.Configuration.GetSection(TrackSplitOptions.SectionName));
builder.Services.AddSingleton<ITrackSplitService, TrackSplitService>();

// ─── Plex integration ─────────────────────────────────────────────
builder.Services.Configure<PlexOptions>(
    builder.Configuration.GetSection(PlexOptions.SectionName));
builder.Services.AddSingleton<PlexClientIdentity>();
builder.Services.AddHttpClient<IPlexAuthClient, PlexAuthClient>(c =>
{
    c.BaseAddress = new Uri(PlexAuthClient.BaseAddress);
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<IPlexServerClient, PlexServerClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // PMS often serves a self-signed cert at *.plex.direct hostnames.
    // The hostnames are valid against Plex's wildcard cert, but in case the
    // local connection uses an IP literal, we accept the chain.
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPlexSessionStore, PlexSessionStore>();
builder.Services.AddScoped<IPlexLibraryService, PlexLibraryService>();

// Persist DataProtection keys so Plex session cookies survive app restarts.
var plexConfig = builder.Configuration.GetSection(PlexOptions.SectionName).Get<PlexOptions>() ?? new PlexOptions();
var keysDir = Path.IsPathRooted(plexConfig.DataProtectionKeysDirectory)
    ? plexConfig.DataProtectionKeysDirectory
    : Path.Combine(builder.Environment.ContentRootPath, plexConfig.DataProtectionKeysDirectory);
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("SyncAudio");

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var contentTypes = new FileExtensionContentTypeProvider
{
    Mappings =
    {
        [".mp3"]     = "audio/mpeg",
        [".flac"]    = "audio/flac",
        [".opus256"] = "audio/ogg",
    }
};

var app = builder.Build();

// Ensure MinIO buckets exist. Non-fatal: if MinIO is briefly unavailable at startup
// the first real request will fail gracefully rather than crashing the process.
if (useMinio)
{
    try
    {
        var store = app.Services.GetRequiredService<IObjectStore>();
        await ((MinioObjectStore)store).EnsureBucketsAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "MinIO bucket initialisation failed — buckets may not exist yet");
    }
}

app.MapDefaultEndpoints();

app.UseForwardedHeaders();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value;
        if (path != null && path.StartsWith("/audio/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "public, max-age=604800";
        }
    }
});
app.UseRouting();
app.MapStaticAssets();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHub<SyncHub>("/synchub");

app.MapGet("/cover/{trackId}", async (string trackId, string? size,
    ICoverArtService cover, HttpResponse response, CancellationToken ct) =>
{
    var result = await cover.GetCoverAsync(trackId, size ?? "full", ct);
    if (result is null) return Results.NotFound();

    response.Headers.CacheControl = "public, max-age=604800";
    return Results.File(result.Value.Data, result.Value.Mime);
});

// ─── Audio delivery ───────────────────────────────────────────────

app.MapGet("/track/{trackId}/audio-url", async (string trackId,
    IObjectStore objectStore, IOptions<StorageOptions> storageOpts, CancellationToken ct) =>
{
    var key = $"local/{trackId}.flac";
    var url = await objectStore.GetPresignedGetUrlAsync(storageOpts.Value.Buckets.Audio, key, TimeSpan.FromHours(1), ct);
    // MinIO returns an absolute http(s) URL → redirect the browser directly to it.
    if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        return Results.Redirect(url);
    // Local provider returns a file path → redirect to the /storage/ passthrough instead.
    return Results.Redirect($"/storage/{Uri.EscapeDataString(storageOpts.Value.Buckets.Audio)}/local/{Uri.EscapeDataString(trackId)}.flac");
});

// Passthrough for local-provider audio/split files (not used in MinIO mode).
app.MapGet("/storage/{bucket}/{**key}", async (string bucket, string key,
    IObjectStore objectStore, HttpResponse response, CancellationToken ct) =>
{
    if (!await objectStore.ExistsAsync(bucket, key, ct))
        return Results.NotFound();
    var stream = await objectStore.OpenReadAsync(bucket, key, ct);
    var ext = Path.GetExtension(key).TrimStart('.').ToLowerInvariant();
    var mime = ext switch { "flac" => "audio/flac", "mp3" => "audio/mpeg", "opus256" => "audio/ogg", "webp" => "image/webp", _ => "application/octet-stream" };
    response.Headers.CacheControl = "public, max-age=3600";
    return Results.Stream(stream, mime, enableRangeProcessing: true);
});

// ─── Plex auth + cover proxy endpoints ────────────────────────────

app.MapPost("/plex/auth/start", async (IPlexAuthClient auth, HttpContext ctx, CancellationToken ct) =>
{
    var pin = await auth.CreatePinAsync(ct);
    return Results.Json(new { pinId = pin.Id, code = pin.Code });
}).DisableAntiforgery();

app.MapGet("/plex/auth/status", async (long pinId,
    IPlexAuthClient auth, IPlexSessionStore store, CancellationToken ct) =>
{
    var pin = await auth.CheckPinAsync(pinId, ct);

    if (!string.IsNullOrEmpty(pin.AuthToken))
    {
        var username = await auth.GetUsernameAsync(pin.AuthToken, ct);
        store.Set(new PlexSession(
            Token: pin.AuthToken,
            MachineIdentifier: string.Empty,
            BaseUri: string.Empty,
            Username: username,
            ResolvedAt: DateTimeOffset.UtcNow));
        return Results.Json(new { authenticated = true, expired = false });
    }

    var expired = pin.ExpiresAt != DateTimeOffset.MinValue && pin.ExpiresAt <= DateTimeOffset.UtcNow;
    return Results.Json(new { authenticated = false, expired });
});

app.MapPost("/plex/auth/logout", (IPlexSessionStore store) =>
{
    store.Clear();
    return Results.Ok();
}).DisableAntiforgery();

app.MapGet("/plex/cover/{serverId}/{ratingKey}", async (string serverId, string ratingKey,
    string? thumb, string? size, IPlexLibraryService plex, IHttpClientFactory httpFactory,
    IObjectStore objectStore, IOptions<StorageOptions> storageOpts,
    IMemoryCache memCache, HttpResponse response, CancellationToken ct) =>
{
    var isFull = size is "full";
    var thumbKey = $"plex/{serverId}/{ratingKey}_thumb.webp";
    var fullKey = $"plex/{serverId}/{ratingKey}_orig";
    var coverBucket = storageOpts.Value.Buckets.Covers;

    var requestedKey = isFull ? fullKey : thumbKey;
    var cacheKey = $"plex-cover:{requestedKey}";

    // 1. Object-store cache check. Thumbs are always WebP; full keeps original format.
    if (memCache.TryGetValue(cacheKey, out (byte[] Data, string Mime) hit) && hit.Data is not null)
    {
        response.Headers.CacheControl = "public, max-age=604800";
        return Results.File(hit.Data, hit.Mime);
    }
    if (await objectStore.ExistsAsync(coverBucket, requestedKey, ct))
    {
        await using var stored = await objectStore.OpenReadAsync(coverBucket, requestedKey, ct);
        using var storedMs = new MemoryStream();
        await stored.CopyToAsync(storedMs, ct);
        var storedBytes = storedMs.ToArray();
        var storedMime = isFull ? CoverMime.Detect(storedBytes) : "image/webp";
        memCache.Set(cacheKey, (storedBytes, storedMime),
            new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
        response.Headers.CacheControl = "public, max-age=604800";
        return Results.File(storedBytes, storedMime);
    }

    // 2. Fetch from Plex
    var session = await plex.EnsureSessionAsync(ct);
    if (session is null || string.IsNullOrEmpty(session.BaseUri)) return Results.NotFound();

    var thumbPath = !string.IsNullOrEmpty(thumb)
        ? thumb
        : $"/library/metadata/{ratingKey}/thumb/0";
    var url = $"{session.BaseUri.TrimEnd('/')}{thumbPath}";

    using var http = httpFactory.CreateClient();
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Add("X-Plex-Token", session.Token);
    using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    if (!res.IsSuccessStatusCode) return Results.NotFound();

    var sourceBytes = await res.Content.ReadAsByteArrayAsync(ct);
    var sourceMime = res.Content.Headers.ContentType?.MediaType is { Length: > 0 } ct1
        ? ct1
        : CoverMime.Detect(sourceBytes);

    // 3. Persist — full is byte-for-byte passthrough, thumb is a small WebP derivative.
    // Best-effort; concurrent writes for the same key are harmless.
    try
    {
        if (!await objectStore.ExistsAsync(coverBucket, fullKey, ct))
        {
            using var fullStream = new MemoryStream(sourceBytes, writable: false);
            await objectStore.PutAsync(coverBucket, fullKey, fullStream, sourceMime, ct);
        }
        if (!await objectStore.ExistsAsync(coverBucket, thumbKey, ct))
        {
            using var thumbStream = CoverProcessor.MakeThumb(sourceBytes);
            await objectStore.PutAsync(coverBucket, thumbKey, thumbStream, "image/webp", ct);
        }
    }
    catch { /* non-fatal */ }

    // 4. Serve the requested size from what we just fetched.
    if (isFull)
    {
        memCache.Set(cacheKey, (sourceBytes, sourceMime),
            new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
        response.Headers.CacheControl = "public, max-age=604800";
        return Results.File(sourceBytes, sourceMime);
    }

    if (await objectStore.ExistsAsync(coverBucket, thumbKey, ct))
    {
        await using var s = await objectStore.OpenReadAsync(coverBucket, thumbKey, ct);
        using var sms = new MemoryStream();
        await s.CopyToAsync(sms, ct);
        var thumbBytes = sms.ToArray();
        memCache.Set(cacheKey, (thumbBytes, "image/webp"),
            new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
        response.Headers.CacheControl = "public, max-age=604800";
        return Results.File(thumbBytes, "image/webp");
    }

    // Thumb derivation failed (e.g. ImageSharp couldn't decode); fall back to source.
    response.Headers.CacheControl = "public, max-age=604800";
    return Results.File(sourceBytes, sourceMime);
});

app.MapGet("/plex/search", async (string q, int? offset, int? limit, IPlexLibraryService plex, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.Json(new { albums = Array.Empty<object>(), total = 0 });
    var off = Math.Max(0, offset ?? 0);
    var size = Math.Clamp(limit ?? 50, 1, 200);
    var (albums, total) = await plex.SearchAlbumsAsync(q, off, size, ct);
    var payload = albums.Select(a => new
    {
        ratingKey = a.RatingKey,
        title = a.Title,
        artist = a.Artist,
        year = a.Year,
        trackCount = a.TrackCount,
        coverUrl = a.CoverUrl,
    });
    return Results.Json(new { albums = payload, total });
});

app.MapGet("/plex/album/{ratingKey}/tracks", async (string ratingKey, IPlexLibraryService plex, CancellationToken ct) =>
{
    var tracks = await plex.GetAlbumTracksAsync(ratingKey, ct);
    var payload = tracks.Select(t => new
    {
        id = t.Id,
        title = t.Title,
        artist = t.Artist,
        album = t.Album,
        audioUrl = t.AudioUrl,
        duration = t.Duration.TotalSeconds,
        palette = t.Palette,
        lossless = t.Lossless,
        spatial = t.Spatial,
        coverUrl = t.CoverUrl,
        source = (int)t.Source,
        remoteSourceUrl = t.RemoteSourceUrl,
        plexServerId = t.PlexServerId,
    });
    return Results.Json(payload);
});

app.MapPost("/plex/prepare", async (PlexPrepareRequest body,
    IPlexLibraryService plex, ITrackSplitService splitter, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(body.Id) || string.IsNullOrEmpty(body.RemoteSourceUrl))
    {
        return Results.BadRequest(new { error = "Missing id or remoteSourceUrl" });
    }
    var token = plex.GetAuthToken();
    if (string.IsNullOrEmpty(token))
    {
        return Results.Json(new { error = "not_connected" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var session = await plex.EnsureSessionAsync(ct);
    var track = new SyncAudio.Core.Models.Track(
        Id: body.Id,
        Title: body.Title ?? string.Empty,
        Artist: body.Artist ?? string.Empty,
        Album: body.Album ?? string.Empty,
        AudioUrl: body.RemoteSourceUrl,
        Duration: TimeSpan.Zero,
        Palette: ["#5EC8FF", "#7A3CFF", "#FF8FB1"],
        Lossless: false,
        Spatial: false,
        CoverUrl: null,
        Source: SyncAudio.Core.Models.TrackSource.Plex,
        RemoteSourceUrl: body.RemoteSourceUrl,
        PlexServerId: session?.MachineIdentifier ?? "");

    try
    {
        ChannelMapping? overrideMapping = null;
        if (body is { FrontL: { } fl, FrontR: { } fr, BackL: { } bl, BackR: { } br })
        {
            overrideMapping = new ChannelMapping(fl, fr, bl, br);
        }

        var result = await splitter.EnsureSplitAsync(
            track,
            overrideMapping,
            $"X-Plex-Token: {token}",
            outputFormatOverride: body.OutputFormat,
            ct);
        return Results.Json(new
        {
            frontUrl = result.FrontUrl,
            backUrl = result.BackUrl,
            channelCount = result.SourceChannelCount,
            channelLayout = result.SourceChannelLayout,
            mapping = new
            {
                frontL = result.EffectiveMapping.FrontL,
                frontR = result.EffectiveMapping.FrontR,
                backL = result.EffectiveMapping.BackL,
                backR = result.EffectiveMapping.BackR,
            },
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
}).DisableAntiforgery();

app.Run();

public sealed record PlexPrepareRequest(
    string Id,
    string? Title,
    string? Artist,
    string? Album,
    string RemoteSourceUrl,
    int? FrontL = null,
    int? FrontR = null,
    int? BackL = null,
    int? BackR = null,
    string? OutputFormat = null);

public partial class Program;