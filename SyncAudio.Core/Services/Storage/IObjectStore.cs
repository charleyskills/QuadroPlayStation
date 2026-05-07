namespace SyncAudio.Core.Services.Storage;

public interface IObjectStore
{
    Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct = default);

    Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken ct = default);

    /// <summary>
    /// Returns a URL the browser can use to GET the object directly (with Range support).
    /// Local provider returns a relative app-tier URL; Minio provider returns a pre-signed S3 URL.
    /// </summary>
    Task<string> GetPresignedGetUrlAsync(string bucket, string key, TimeSpan ttl, CancellationToken ct = default);

    Task DeleteAsync(string bucket, string key, CancellationToken ct = default);

    IAsyncEnumerable<string> ListKeysAsync(string bucket, string prefix, CancellationToken ct = default);
}
