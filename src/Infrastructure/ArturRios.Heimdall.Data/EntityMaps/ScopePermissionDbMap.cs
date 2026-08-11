using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class ScopePermissionDbMap
{
    public static void Configure(this EntityTypeBuilder<ScopePermission> scopePermission)
    {
        scopePermission.ToTable("scope_permission");

        scopePermission.HasKey(x => x.Id);

        scopePermission.Property(x => x.PublicId).IsRequired();
        scopePermission.HasIndex(x => x.PublicId).IsUnique();

        scopePermission.Property(x => x.Name).IsRequired();

        scopePermission.Property(x => x.Description);

        scopePermission.Property(x => x.IncludeAsJwtClaim).HasDefaultValue(false);
        scopePermission.Property(x => x.IsDeleted).HasDefaultValue(false);

        scopePermission.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        scopePermission.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        // A permission belongs to exactly one scope; hard-deleting the scope cascades to its
        // permissions.
        scopePermission.HasOne(x => x.Scope)
            .WithMany(s => s.ScopePermissions)
            .HasForeignKey(x => x.ScopeId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}