using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Audit;

public interface IAuditService
{
    /// <summary>
    /// Adds an audit entry to the change tracker. Does NOT call SaveChanges — the caller
    /// saves once so the audit row commits atomically with the change it describes.
    /// </summary>
    void Log(long? userId, string action, string? entity = null, long? entityId = null,
        string? details = null, string? ip = null);
}

public class AuditService : IAuditService
{
    private readonly AppDbContext _db;

    public AuditService(AppDbContext db) => _db = db;

    public void Log(long? userId, string action, string? entity = null, long? entityId = null,
        string? details = null, string? ip = null)
    {
        _db.AuditLog.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            Details = details,
            IpAddress = ip,
        });
    }
}
