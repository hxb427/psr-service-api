using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles");

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");

        b.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(50)
            .IsRequired();
        b.HasIndex(x => x.Name).IsUnique();

        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);

        // Fixed ids (gaps at 5 = inward_manager and 7 = dispatch_manager, both removed).
        b.HasData(RoleNames.Seed.Select(r => new Role { Id = r.Id, Name = r.Name, Description = null }).ToArray());
    }
}
