namespace SyncAudio.AppHost;

public class Containers
{
    public const string Minio = "Minio";
    public const string SyncAudio = "SyncAudio";

    public static string ToDataVolume(string containerName) => $"{containerName}-data";
}