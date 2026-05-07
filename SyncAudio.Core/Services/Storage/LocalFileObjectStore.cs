using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

namespace SyncAudio.Core.Services.Storage;

/// <summary>
/// Filesystem-backed object store rooted at <see cref="LocalStorageOptions.RootPath"/>.
/// Bucket names become subdirectories; key slashes become OS path separators.
/// Used in local dev and as a fallback when MinIO is not configured.
/// </summary>
public sealed class LocalFileObjectStore(IOptions<StorageOptions> options, IWebHostEnvironment env) : IObjectStore
{
    private string Root => Path.IsPathRooted(options.Value.Local.RootPath)
        ? options.Value.Local.RootPath
        : Path.Combine(env.ContentRootPath, options.Value.Local.RootPath);

    private string Resolve(string bucket, string key)
        => Path.Combine(Root, bucket, key.Replace('/', Path.DirectorySeparatorChar));

    public Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct = default)
        => Task.FromResult(File.Exists(Resolve(bucket, key)));

    public async Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = Resolve(bucket, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken ct = default)
        => Task.FromResult<Stream>(File.OpenRead(Resolve(bucket, key)));

    /// <summary>
    /// Returns the absolute filesystem path — ffmpeg accepts local paths directly.
    /// Client-facing audio/split URLs are remapped to /storage/ passthrough by the caller.
    /// </summary>
    public Task<string> GetPresignedGetUrlAsync(string bucket, string key, TimeSpan ttl, CancellationToken ct = default)
        => Task.FromResult(Resolve(bucket, key));

    public Task DeleteAsync(string bucket, string key, CancellationToken ct = default)
    {
        var path = Resolve(bucket, key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListKeysAsync(
        string bucket, string prefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var bucketDir = Path.Combine(Root, bucket);
        var searchDir = string.IsNullOrEmpty(prefix)
            ? bucketDir
            : Path.Combine(bucketDir, prefix.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar));

        if (!Directory.Exists(searchDir)) yield break;

        foreach (var file in Directory.EnumerateFiles(searchDir, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(bucketDir, file).Replace(Path.DirectorySeparatorChar, '/');
            yield return rel;
        }
    }
}
