using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace SyncAudio.Core.Services.CoverArt;

/// <summary>
/// Builds derived WebP cover variants from raw source bytes.
/// </summary>
public static class CoverProcessor
{
    private static readonly WebpEncoder ThumbEncoder = new() { Quality = 80 };
    private static readonly WebpEncoder LargeEncoder = new() { FileFormat = WebpFileFormatType.Lossless };

    /// <param name="sourceBytes">Raw image bytes (any format ImageSharp can decode).</param>
    /// <returns>A WebP thumbnail (max 256×256) positioned at offset 0. Caller owns the stream.</returns>
    public static MemoryStream MakeThumb(byte[] sourceBytes)
    {
        using var image = Image.Load(sourceBytes);
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;

        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(256, 256),
            Mode = ResizeMode.Max,
        }));

        var thumb = new MemoryStream();
        image.Save(thumb, ThumbEncoder);
        thumb.Position = 0;
        return thumb;
    }

    /// <param name="sourceBytes">Raw image bytes (any format ImageSharp can decode).</param>
    /// <returns>A lossless WebP (max 1500×1500) positioned at offset 0. Caller owns the stream.</returns>
    public static MemoryStream MakeLarge(byte[] sourceBytes)
    {
        using var image = Image.Load(sourceBytes);
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;

        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(1500, 1500),
            Mode = ResizeMode.Max,
        }));

        var large = new MemoryStream();
        image.Save(large, LargeEncoder);
        large.Position = 0;
        return large;
    }
}
