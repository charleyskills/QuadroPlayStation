namespace SyncAudio.Core.Services.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "Local";
    public LocalStorageOptions Local { get; set; } = new();
    public MinioStorageOptions Minio { get; set; } = new();
    public BucketNames Buckets { get; set; } = new();
}

public sealed class LocalStorageOptions
{
    public string RootPath { get; set; } = "App_Data/storage";
}

public sealed class MinioStorageOptions
{
    public string Endpoint { get; set; } = "localhost:9000";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public bool UseSsl { get; set; } = false;
    public string Region { get; set; } = "us-east-1";
}

public sealed class BucketNames
{
    public string Audio { get; set; } = "syncaudio-audio";
    public string Splits { get; set; } = "syncaudio-splits";
    public string Covers { get; set; } = "syncaudio-covers";
}
