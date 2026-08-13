using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.ToTable("stock_movements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.PartId).HasColumnName("part_id");
        b.Property(x => x.MovementType).HasColumnName("movement_type").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Quantity).HasColumnName("quantity");
        b.Property(x => x.TechnicianId).HasColumnName("technician_id");
        b.Property(x => x.ReferenceType).HasColumnName("reference_type").HasMaxLength(30);
        b.Property(x => x.ReferenceId).HasColumnName("reference_id");
        b.Property(x => x.InvoiceNo).HasColumnName("invoice_no").HasMaxLength(50);
        b.Property(x => x.Source).HasColumnName("source").HasMaxLength(100);
        b.Property(x => x.SerialNo).HasColumnName("serial_no").HasMaxLength(100);
        b.Property(x => x.PerformedByUserId).HasColumnName("performed_by_user_id");
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PartId);
        b.HasIndex(x => x.TechnicianId);
        b.HasIndex(x => x.CreatedAt);
        b.HasIndex(x => new { x.ReferenceType, x.ReferenceId });
    }
}

public class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> b)
    {
        b.ToTable("stock_balances");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.PartId).HasColumnName("part_id");
        b.Property(x => x.TechnicianId).HasColumnName("technician_id");
        b.Property(x => x.OnHand).HasColumnName("on_hand");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PartId, x.TechnicianId }).IsUnique();
        b.HasIndex(x => x.TechnicianId);
    }
}

public class StockRequestConfiguration : IEntityTypeConfiguration<StockRequest>
{
    public void Configure(EntityTypeBuilder<StockRequest> b)
    {
        b.ToTable("stock_requests");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.RequestNo).HasColumnName("request_no").HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.RequestNo).IsUnique();
        b.Property(x => x.RequestedByUserId).HasColumnName("requested_by_user_id");
        b.Property(x => x.RequestDate).HasColumnName("request_date");
        b.Property(x => x.PartId).HasColumnName("part_id");
        b.Property(x => x.QtyRequested).HasColumnName("qty_requested");
        b.Property(x => x.QtyIssued).HasColumnName("qty_issued").HasDefaultValue(0);
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.IssuedByUserId).HasColumnName("issued_by_user_id");
        b.Property(x => x.IssuedDate).HasColumnName("issued_date");
        b.Property(x => x.Courier).HasColumnName("courier").HasMaxLength(80);
        b.Property(x => x.TrackingNo).HasColumnName("tracking_no").HasMaxLength(80);
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RequestedByUserId);
        b.HasIndex(x => x.Status);
    }
}

public class StockReturnConfiguration : IEntityTypeConfiguration<StockReturn>
{
    public void Configure(EntityTypeBuilder<StockReturn> b)
    {
        b.ToTable("stock_returns");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ReturnNo).HasColumnName("return_no").HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.ReturnNo).IsUnique();
        b.Property(x => x.TechnicianId).HasColumnName("technician_id");
        b.Property(x => x.PartId).HasColumnName("part_id");
        b.Property(x => x.Qty).HasColumnName("qty");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AcknowledgedByUserId).HasColumnName("acknowledged_by_user_id");
        b.Property(x => x.AcknowledgedDate).HasColumnName("acknowledged_date");
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.Courier).HasColumnName("courier").HasMaxLength(80);
        b.Property(x => x.TrackingNo).HasColumnName("tracking_no").HasMaxLength(80);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TechnicianId);
        b.HasIndex(x => x.Status);
    }
}

public class NumberSequenceConfiguration : IEntityTypeConfiguration<NumberSequence>
{
    public void Configure(EntityTypeBuilder<NumberSequence> b)
    {
        b.ToTable("number_sequences");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasColumnName("key").HasMaxLength(40);
        b.Property(x => x.Prefix).HasColumnName("prefix").HasMaxLength(10);
        b.Property(x => x.NextValue).HasColumnName("next_value");
        b.Property(x => x.Year).HasColumnName("year");

        b.HasData(
            new NumberSequence { Key = SequenceKeys.StockRequest, Prefix = "REQ", NextValue = 1 },
            new NumberSequence { Key = SequenceKeys.StockReturn, Prefix = "RET", NextValue = 1 },
            new NumberSequence { Key = SequenceKeys.Service, Prefix = "SVC", NextValue = 1 },
            // Year-scoped document sequences (clean sequential, reset annually). Year is bumped on first use.
            new NumberSequence { Key = SequenceKeys.ProformaInvoice, Prefix = "PI", NextValue = 1, Year = 2026 },
            new NumberSequence { Key = SequenceKeys.Invoice, Prefix = "INV", NextValue = 1, Year = 2026 },
            new NumberSequence { Key = SequenceKeys.DeliveryChallan, Prefix = "DC", NextValue = 1, Year = 2026 },
            new NumberSequence { Key = SequenceKeys.Transfer, Prefix = "TRF", NextValue = 1 },
            new NumberSequence { Key = SequenceKeys.FieldService, Prefix = "FSV", NextValue = 1 },
            new NumberSequence { Key = SequenceKeys.FieldSale, Prefix = "FSL", NextValue = 1 },
            new NumberSequence { Key = SequenceKeys.SpareSale, Prefix = "SAL", NextValue = 1 },
            // The row itself was added by the AddSpareSaleReturns migration but never declared here.
            // Every key a NextAsync caller uses MUST appear in this list: the model snapshot is built
            // from it, so a key that is missing looks to EF like a row that should not exist, and the
            // next generated migration deletes it — taking spare-sale returns down with it.
            new NumberSequence { Key = SequenceKeys.SpareSaleReturn, Prefix = "SRT", NextValue = 1 });
    }
}
