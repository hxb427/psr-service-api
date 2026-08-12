using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<AuditLog> AuditLog => Set<AuditLog>();
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<ServiceCharge> ServiceCharges => Set<ServiceCharge>();
    public DbSet<Dealer> Dealers => Set<Dealer>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockRequest> StockRequests => Set<StockRequest>();
    public DbSet<StockReturn> StockReturns => Set<StockReturn>();
    public DbSet<ComponentSerial> ComponentSerials => Set<ComponentSerial>();
    public DbSet<SerialStatusHistory> SerialStatusHistory => Set<SerialStatusHistory>();
    public DbSet<StockIssueSerial> StockIssueSerials => Set<StockIssueSerial>();
    public DbSet<StockIssueAck> StockIssueAcks => Set<StockIssueAck>();
    public DbSet<StockReturnSerial> StockReturnSerials => Set<StockReturnSerial>();
    public DbSet<TechnicianTransfer> TechnicianTransfers => Set<TechnicianTransfer>();
    public DbSet<TechnicianTransferLine> TechnicianTransferLines => Set<TechnicianTransferLine>();
    public DbSet<TechnicianTransferSerial> TechnicianTransferSerials => Set<TechnicianTransferSerial>();
    public DbSet<FieldService> FieldServices => Set<FieldService>();
    public DbSet<FieldServiceLine> FieldServiceLines => Set<FieldServiceLine>();
    public DbSet<FieldSale> FieldSales => Set<FieldSale>();
    public DbSet<FieldSaleLine> FieldSaleLines => Set<FieldSaleLine>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
    public DbSet<ServiceJob> Services => Set<ServiceJob>();
    public DbSet<ServiceLine> ServiceLines => Set<ServiceLine>();
    public DbSet<ServiceStatusHistory> ServiceStatusHistory => Set<ServiceStatusHistory>();
    public DbSet<SpareSale> SpareSales => Set<SpareSale>();
    public DbSet<SpareSaleLine> SpareSaleLines => Set<SpareSaleLine>();
    public DbSet<SpareSaleReturn> SpareSaleReturns => Set<SpareSaleReturn>();
    public DbSet<SpareSaleReturnLine> SpareSaleReturnLines => Set<SpareSaleReturnLine>();
    public DbSet<ServiceDocument> ServiceDocuments => Set<ServiceDocument>();
    public DbSet<ServiceDocumentLine> ServiceDocumentLines => Set<ServiceDocumentLine>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    public override int SaveChanges()
    {
        StampTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void StampTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<ITimestamps>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
