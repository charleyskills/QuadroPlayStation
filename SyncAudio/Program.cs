using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using SyncAudio.Components;
using SyncAudio.Components.Pages.NowPlaying;
using SyncAudio.Hubs;
using SyncAudio.Services;
using SyncAudio.Services.Plex;
using SyncAudio.Services.TrackSplit;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<ICoverArtService, CoverArtService>();
builder.Services.AddSingleton<ITrackLibraryService, TrackLibraryService>();
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
        [".flac"] = "audio/flac"
    }
};

var app = builder.Build();

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

app.MapGet("/cover/{id}", (string id, ICoverArtService cover, HttpResponse response) =>
{
    if (!cover.TryGetCover(id, out var data, out var mime))
    {
        return Results.NotFound();
    }

    response.Headers.CacheControl = "public, max-age=604800";
    return Results.File(data, mime);
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

app.MapGet("/plex/cover/{ratingKey}", async (string ratingKey,
    string? thumb, IPlexLibraryService plex, IHttpClientFactory httpFactory,
    IWebHostEnvironment env, HttpResponse response, CancellationToken ct) =>
{
    // 1. Disk cache — ratingKey from Plex is always numeric, safe as a filename
    var cacheDir = Path.Combine(env.ContentRootPath, "App_Data", "cover-cache");
    Directory.CreateDirectory(cacheDir);
    var cacheBase = Path.Combine(cacheDir, ratingKey);
    foreach (var ext in new[] { ".jpg", ".png", ".webp" })
    {
        var cached = cacheBase + ext;
        if (!File.Exists(cached)) continue;
        response.Headers.CacheControl = "public, max-age=604800";
        var mime = ext switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        return Results.File(cached, mime);
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

    var contentType = res.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
    var diskExt = contentType switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
    var bytes = await res.Content.ReadAsByteArrayAsync(ct);

    // 3. Persist to disk — best-effort; concurrent writes for the same key are harmless
    try { await File.WriteAllBytesAsync(cacheBase + diskExt, bytes, ct); } catch { /* ignore */ }

    response.Headers.CacheControl = "public, max-age=604800";
    return Results.File(bytes, contentType);
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

    var track = new SyncAudio.Models.Track(
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
        Source: SyncAudio.Models.TrackSource.Plex,
        RemoteSourceUrl: body.RemoteSourceUrl);

    try
    {
        ChannelMapping? overrideMapping = null;
        if (body is { FrontL: { } fl, FrontR: { } fr, BackL: { } bl, BackR: { } br })
        {
            overrideMapping = new ChannelMapping(fl, fr, bl, br);
        }

        var result = await splitter.EnsureSplitAsync(track, overrideMapping, $"X-Plex-Token: {token}", ct);
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
    int? BackR = null);

public partial class Program;