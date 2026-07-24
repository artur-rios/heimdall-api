using ArturRios.IdentityManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.IdentityManager.Data.EntityMaps;

internal static class ScopeOwnerDbMap
{
    public static void Configure(this EntityTypeBuilder<ScopeOwner> scopeOwner)
    {
        scopeOwner.ToTable("scope_owner");

        // Composite key (ScopeId, PersonId); no surrogate Id and no PublicId (§4.5).
        scopeOwner.HasKey(x => new { x.ScopeId, x.PersonId });

        // A scope's owners are removed when the scope is hard-deleted (NFR-08).
        scopeOwner.HasOne(x => x.Scope)
            .WithMany(s => s.Owners)
            .HasForeignKey(x => x.ScopeId)
            .OnDelete(DeleteBehavior.Cascade);

        // A ScopeAdmin's ownership rows are removed when the person is hard-deleted (UC-10).
        scopeOwner.HasOne(x => x.Person)
            .WithMany(p => p.ScopeOwnerships)
            .HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
