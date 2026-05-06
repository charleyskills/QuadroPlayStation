var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.SyncAudio>("syncaudio");

builder.Build().Run();
