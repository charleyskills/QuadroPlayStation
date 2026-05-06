using System.Text.Json.Serialization;

namespace SyncAudio.Services.Plex;

public sealed record PlexPin(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("clientIdentifier")] string ClientIdentifier,
    [property: JsonPropertyName("authToken")] string? AuthToken,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

public sealed record PlexConnection(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("local")] bool Local,
    [property: JsonPropertyName("relay")] bool Relay);

public sealed record PlexResource(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("product")] string? Product,
    [property: JsonPropertyName("clientIdentifier")] string ClientIdentifier,
    [property: JsonPropertyName("provides")] string Provides,
    [property: JsonPropertyName("owned")] bool Owned,
    [property: JsonPropertyName("accessToken")] string? AccessToken,
    [property: JsonPropertyName("connections")] IReadOnlyList<PlexConnection> Connections);

public sealed record PlexLibrarySection(
    string Key,
    string Title,
    string Type);

public sealed record PlexTrackHit(
    string RatingKey,
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    string PartKey,
    string? Container,
    string? ThumbKey);

public sealed record PlexAlbumHit(
    string RatingKey,
    string Title,
    string Artist,
    int? Year,
    int TrackCount,
    string? ThumbKey);

public sealed record PlexSession(
    string Token,
    string MachineIdentifier,
    string BaseUri,
    string? Username,
    DateTimeOffset ResolvedAt,
    string? MusicSectionKey = null);
