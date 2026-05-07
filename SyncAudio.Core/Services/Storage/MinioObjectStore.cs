using System.Runtime.CompilerServices;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Microsoft.Extensions.Options;

namespace SyncAudio.Core.Services.Storage;

/// <summary>
/// S3-compatible object store backed by MinIO (or AWS S3 / Cloudflare R2).
/// Buckets must exist before use — created by the minio-init container in docker-compose,
/// or via <see cref="EnsureBucketsAsync"/> at startup.
/// </summary>
public sealed class MinioObjectStore(IMinioClient client, IOptions<StorageOptions> options) : IObjectStore
{
    private StorageOptions Opts => options.Value;

    public async Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct = default)
    {
        try
        {
            await client.StatObjectAsync(
                new StatObjectArgs().WithBucket(bucket).WithObject(key), ct);
            return true;
        }
        catch (ObjectNotFoundException) { return false; }
        catch (BucketNotFoundException) { return false; }
        catch (MinioException) { return false; }
    }

    public async Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct = default)
    {
        var upload = content;
        long size;

        if (content.CanSeek)
        {
            size = content.Length - content.Position;
        }
        else
        {
            var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            ms.Position = 0;
            upload = ms;
            size = ms.Length;
        }

        await client.PutObjectAsync(
            new PutObjectArgs()
                .WithBucket(bucket)
                .WithObject(key)
                .WithStreamData(upload)
                .WithObjectSize(size)
                .WithContentType(contentType),
            ct);
    }

    public async Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        await client.GetObjectAsync(
            new GetObjectArgs()
                .WithBucket(bucket)
                .WithObject(key)
                .WithCallbackStream(async (stream, token) => await stream.CopyToAsync(ms, token)),
            ct);
        ms.Position = 0;
        return ms;
    }

    public async Task<string> GetPresignedGetUrlAsync(string bucket, string key, TimeSpan ttl, CancellationToken ct = default)
    {
        return await client.PresignedGetObjectAsync(
            new PresignedGetObjectArgs()
                .WithBucket(bucket)
                .WithObject(key)
                .WithExpiry((int)ttl.TotalSeconds));
    }

    public async Task DeleteAsync(string bucket, string key, CancellationToken ct = default)
    {
        await client.RemoveObjectAsync(
            new RemoveObjectArgs().WithBucket(bucket).WithObject(key), ct);
    }

    public async IAsyncEnumerable<string> ListKeysAsync(
        string bucket, string prefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var args = new ListObjectsArgs()
            .WithBucket(bucket)
            .WithPrefix(prefix)
            .WithRecursive(true);

        await foreach (var item in client.ListObjectsEnumAsync(args, ct))
        {
            yield return item.Key;
        }
    }

    /// <summary>
    /// Creates the three app buckets if they don't exist. Safe to call repeatedly.
    /// Called at startup when Provider=Minio and docker-compose minio-init is not used.
    /// </summary>
    public async Task EnsureBucketsAsync(CancellationToken ct = default)
    {
        var buckets = new[]
        {
            Opts.Buckets.Audio,
            Opts.Buckets.Splits,
            Opts.Buckets.Covers,
        };

        foreach (var bucket in buckets)
        {
            var exists = await client.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(bucket), ct);
            
            if (!exists)
            {
                await client.MakeBucketAsync(
                    new MakeBucketArgs().WithBucket(bucket), ct);
            }
        }
    }
}
