using ArturRios.IdentityManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.IdentityManager.Data.EntityMaps;

internal static class RoleDbMap
{
    public static void Configure(this EntityTypeBuilder<Role> role)
    {
        role.HasKey(x => x.Id);

        role.Property(x => x.PublicId).IsRequired();
        role.HasIndex(x => x.PublicId).IsUnique();

        role.Property(x => x.Name).IsRequired();

        // Role name is unique — User | ScopeAdmin | SystemAdmin (§4.3).
        role.HasIndex(x => x.Name).IsUnique();

        // The Person -> Role relationship is configured in PersonDbMap.
    }
}
