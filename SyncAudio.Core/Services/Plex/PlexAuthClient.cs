using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SyncAudio.Core.Services.Plex;

public sealed class PlexAuthClient(
    HttpClient http,
    PlexClientIdentity identity,
    IOptions<PlexOptions> options,
    ILogger<PlexAuthClient> logger)
    : IPlexAuthClient
{
    private readonly PlexOptions _opts = options.Value;

    public const string BaseAddress = "https://plex.tv/";

    public async Task<PlexPin> CreatePinAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v2/pins?strong=false");
        ApplyHeaders(req);
        req.Content = new StringContent(string.Empty);

        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var pin = await res.Content.ReadFromJsonAsync<PlexPin>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Plex returned an empty pin response");
        logger.LogInformation("Created Plex pin {Id} (code={Code}, expiresAt={ExpiresAt})", pin.Id, pin.Code, pin.ExpiresAt);
        return pin;
    }

    public async Task<PlexPin> CheckPinAsync(long pinId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/v2/pins/{pinId}");
        ApplyHeaders(req);

        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            // Plex returns 404 when the pin has expired or never existed for this client identifier.
            return new PlexPin(pinId, string.Empty, identity.Value, null, DateTimeOffset.MinValue);
        }
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<PlexPin>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Plex returned an empty pin response");
    }

    public async Task<string?> GetUsernameAsync(string token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/v2/user");
        ApplyHeaders(req);
        req.Headers.Add("X-Plex-Token", token);

        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogDebug("Failed to read Plex user: {Status}", res.StatusCode);
            return null;
        }

        try
        {
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String)
            {
                return u.GetString();
            }
            if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            {
                return t.GetString();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse Plex user response");
        }
        return null;
    }

    private void ApplyHeaders(HttpRequestMessage req)
    {
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("X-Plex-Client-Identifier", identity.Value);
        req.Headers.Add("X-Plex-Product", _opts.Product);
        req.Headers.Add("X-Plex-Device", _opts.DeviceName);
        req.Headers.Add("X-Plex-Device-Name", _opts.DeviceName);
        req.Headers.Add("X-Plex-Platform", _opts.Platform);
        req.Headers.Add("X-Plex-Version", _opts.Version);
    }
}
