using Microsoft.Extensions.Options;

namespace SyncAudio.Services.Plex;

public sealed class PlexClientIdentity
{
    private readonly string _value;

    public PlexClientIdentity(IWebHostEnvironment env, IOptions<PlexOptions> options, ILogger<PlexClientIdentity> logger)
    {
        var rel = options.Value.ClientIdentifierFile;
        var path = Path.IsPathRooted(rel) ? rel : Path.Combine(env.ContentRootPath, rel);

        if (File.Exists(path))
        {
            var text = File.ReadAllText(path).Trim();
            if (Guid.TryParse(text, out var existing))
            {
                _value = existing.ToString("D");
                logger.LogInformation("Loaded Plex client identifier from {Path}", path);
                return;
            }
            logger.LogWarning("Plex client identifier file at {Path} was malformed; regenerating", path);
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var generated = Guid.NewGuid().ToString("D");
        File.WriteAllText(path, generated);
        _value = generated;
        logger.LogInformation("Generated new Plex client identifier at {Path}", path);
    }

    public string Value => _value;
}
