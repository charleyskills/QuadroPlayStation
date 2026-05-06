namespace SyncAudio.Components.Pages.NowPlaying;

internal static class NowPlayingHelpers
{
    public static string GenerateRoomId()
    {
        Span<byte> bytes = stackalloc byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return string.Create(7, bytes.ToArray(), static (span, b) =>
        {
            const string c = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            span[0] = c[b[0] % 26]; span[1] = c[b[1] % 26]; span[2] = c[b[2] % 26];
            span[3] = '-';
            span[4] = c[b[3] % 26]; span[5] = c[b[4] % 26]; span[6] = c[b[5] % 26];
        });
    }
}
