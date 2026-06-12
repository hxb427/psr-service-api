using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PSR.Service.Api.Data;

namespace PSR.Service.Api.Auth;

public class UserTokenVersionCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly IMemoryCache _cache;

    public UserTokenVersionCache(IMemoryCache cache) => _cache = cache;

    public async Task<int?> GetCurrentAsync(long userId, AppDbContext db, CancellationToken ct)
    {
        if (_cache.TryGetValue<int>(Key(userId), out var cached))
            return cached;

        var snapshot = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.TokenVersion, u.IsActive })
            .FirstOrDefaultAsync(ct);

        if (snapshot is null || !snapshot.IsActive)
            return null;

        _cache.Set(Key(userId), snapshot.TokenVersion, Ttl);
        return snapshot.TokenVersion;
    }

    public void Invalidate(long userId) => _cache.Remove(Key(userId));

    private static string Key(long userId) => $"user:tv:{userId}";
}
