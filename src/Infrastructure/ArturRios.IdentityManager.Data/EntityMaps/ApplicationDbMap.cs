using ArturRios.IdentityManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.IdentityManager.Data.EntityMaps;

internal static class ApplicationDbMap
{
    public static void Configure(this EntityTypeBuilder<Application> application)
    {
        application.HasKey(x => x.Id);

        application.Property(x => x.PublicId).IsRequired();
        application.HasIndex(x => x.PublicId).IsUnique();

        application.Property(x => x.Name).IsRequired();

        application.Property(x => x.IsDeleted).HasDefaultValue(false);

        application.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        application.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        // Every application belongs to exactly one scope (FR-AP-02); hard-deleting the scope
        // cascades to its applications (NFR-08).
        application.HasOne(x => x.Scope)
            .WithMany(s => s.Applications)
            .HasForeignKey(x => x.ScopeId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // Every application has exactly one owning person (FR-AP-03); hard-deleting the owner
        // cascades to the applications they own (NFR-11).
        application.HasOne(x => x.Owner)
            .WithMany(p => p.OwnedApplications)
            .HasForeignKey(x => x.OwnerId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
