using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace SyncAudio.Services.Plex;

public sealed class PlexSessionStore(
    IHttpContextAccessor httpContextAccessor,
    IDataProtectionProvider dataProtection,
    IWebHostEnvironment env,
    ILogger<PlexSessionStore> logger)
    : IPlexSessionStore
{
    public const string CookieName = "plex_session";
    private const string Purpose = "SyncAudio.Plex.Session.v1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    private readonly IDataProtector _protector = dataProtection.CreateProtector(Purpose);

    public PlexSession? Get()
    {
        var ctx = httpContextAccessor.HttpContext;
        if (ctx is null) return null;
        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var protectedValue) || string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            var json = _protector.Unprotect(protectedValue);
            return JsonSerializer.Deserialize<PlexSession>(json);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read Plex session cookie; treating as missing");
            return null;
        }
    }

    public void Set(PlexSession session)
    {
        var ctx = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("Cannot set Plex session: no HttpContext");
        var json = JsonSerializer.Serialize(session);
        var protectedValue = _protector.Protect(json);

        ctx.Response.Cookies.Append(CookieName, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = !env.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.Add(Lifetime),
            IsEssential = true,
            Path = "/",
        });
    }

    public void Clear()
    {
        var ctx = httpContextAccessor.HttpContext;
        if (ctx is null) return;
        ctx.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = !env.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
    }
}
