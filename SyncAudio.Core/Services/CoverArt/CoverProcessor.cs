using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace SyncAudio.Core.Services.CoverArt;

/// <summary>
/// Builds the lossy 256-px WebP thumbnail used in grid views. The full-size cover is
/// stored verbatim by the caller (no transcoding), so this type only deals with thumbs.
/// </summary>
public static class CoverProcessor
{
    private static readonly WebpEncoder ThumbEncoder = new() { Quality = 80 };

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
}
