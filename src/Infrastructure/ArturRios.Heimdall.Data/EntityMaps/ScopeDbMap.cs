using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class ScopeDbMap
{
    public static void Configure(this EntityTypeBuilder<Scope> scope)
    {
        scope.ToTable("scope");

        scope.HasKey(x => x.Id);

        scope.Property(x => x.PublicId).IsRequired();
        scope.HasIndex(x => x.PublicId).IsUnique();

        scope.Property(x => x.Name).IsRequired();

        // Scope name is unique (FR-SC-01, AF-01a / AF-03b).
        scope.HasIndex(x => x.Name).IsUnique();

        scope.Property(x => x.IsDeleted).HasDefaultValue(false);
        scope.Property(x => x.GoogleSignInEnabled).HasDefaultValue(false);

        scope.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        scope.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        // Relationships to Application, GoogleUser, ScopeOwner and ScopeUser are configured from
        // their respective dependent-side maps.
    }
}
