using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class IndianStateConfiguration : IEntityTypeConfiguration<IndianState>
{
    public void Configure(EntityTypeBuilder<IndianState> b)
    {
        b.ToTable("indian_states");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        b.Property(x => x.SortOrder).HasColumnName("sort_order").HasDefaultValue(0);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

public class DistrictConfiguration : IEntityTypeConfiguration<District>
{
    public void Configure(EntityTypeBuilder<District> b)
    {
        b.ToTable("districts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.StateId).HasColumnName("state_id");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();

        b.HasOne(x => x.State).WithMany().HasForeignKey(x => x.StateId).OnDelete(DeleteBehavior.Restrict);
        // Two districts of the same name in one state is a data error; the same name in two different
        // states is not (Aurangabad is in both Maharashtra and Bihar).
        b.HasIndex(x => new { x.StateId, x.Name }).IsUnique();
    }
}
