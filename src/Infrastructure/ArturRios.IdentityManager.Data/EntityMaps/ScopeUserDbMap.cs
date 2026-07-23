using ArturRios.IdentityManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.IdentityManager.Data.EntityMaps;

internal static class ScopeUserDbMap
{
    public static void Configure(this EntityTypeBuilder<ScopeUser> scopeUser)
    {
        // Composite key (ScopeId, PersonId); no surrogate Id and no PublicId (§4.6).
        scopeUser.HasKey(x => new { x.ScopeId, x.PersonId });

        // A scope's users are removed when the scope is hard-deleted (NFR-08).
        scopeUser.HasOne(x => x.Scope)
            .WithMany(s => s.Users)
            .HasForeignKey(x => x.ScopeId)
            .OnDelete(DeleteBehavior.Cascade);

        // One-to-one with Person: a User belongs to exactly one scope, so PersonId is unique across
        // this table (§4.6). The single-navigation reciprocal is Person.ScopeMembership. The
        // membership row is removed when the person is hard-deleted (UC-10).
        scopeUser.HasOne(x => x.Person)
            .WithOne(p => p.ScopeMembership)
            .HasForeignKey<ScopeUser>(x => x.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
