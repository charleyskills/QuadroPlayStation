using SyncAudio.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var minioUser = builder.AddParameter("minio-user");
var minioPassword = builder.AddParameter("minio-password", secret: true);

var minio = builder
    .AddMinioContainer(Containers.Minio, rootUser: minioUser, rootPassword: minioPassword)
    .WithDataVolume(Containers.ToDataVolume(Containers.Minio))
    .WithLifetime(ContainerLifetime.Persistent);

builder.AddProject<Projects.SyncAudio_Client>(Containers.SyncAudio)
    .WithReference(minio)
    .WaitFor(minio);

builder.Build().Run();
