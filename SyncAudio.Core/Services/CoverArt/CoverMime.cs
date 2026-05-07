namespace SyncAudio.Core.Services.CoverArt;

/// <summary>
/// Detects image MIME type from the raw bytes' magic numbers. The cover-art pipeline
/// stores original embedded picture bytes verbatim (no transcoding) and resolves the
/// content type at serve time, so the storage key can stay format-agnostic.
/// </summary>
public static class CoverMime
{
    public const string Fallback = "application/octet-stream";

    public static string Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";

        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";

        if (bytes.Length >= 6 &&
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
            bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a')
            return "image/gif";

        // ISO-BMFF (HEIF/AVIF): bytes 4..7 == "ftyp", bytes 8..11 = brand
        if (bytes.Length >= 12 &&
            bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p')
        {
            var brand = $"{(char)bytes[8]}{(char)bytes[9]}{(char)bytes[10]}{(char)bytes[11]}";
            return brand switch
            {
                "avif" or "avis" => "image/avif",
                "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" or "mif1" or "msf1" => "image/heic",
                _ => Fallback,
            };
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            return "image/bmp";

        return Fallback;
    }
}
