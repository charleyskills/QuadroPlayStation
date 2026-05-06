namespace SyncAudio.Services.Plex;

public sealed class PlexOptions
{
    public const string SectionName = "Plex";

    public string Product { get; set; } = "SyncAudio";
    public string DeviceName { get; set; } = "SyncAudio Server";
    public string Platform { get; set; } = "Web";
    public string Version { get; set; } = "1.0";
    public string ClientIdentifierFile { get; set; } = "App_Data/plex-client-id";
    public string DataProtectionKeysDirectory { get; set; } = "App_Data/keys";
}
