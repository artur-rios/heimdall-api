using ArturRios.IdentityManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.IdentityManager.Data.EntityMaps;

internal static class PersonDbMap
{
    public static void Configure(this EntityTypeBuilder<Person> person)
    {
        person.HasKey(x => x.Id);

        person.Property(x => x.PublicId).IsRequired();
        person.HasIndex(x => x.PublicId).IsUnique();

        person.Property(x => x.Name).IsRequired();

        person.Property(x => x.Email).IsRequired();

        // Email uniqueness is conditional — per-scope for Users (via SCOPE_USER), system-wide for
        // ScopeAdmins/SystemAdmins (FR-PE-09) — and cannot be expressed as a single unique index,
        // so it is enforced in the application layer. This non-unique index only speeds up lookups.
        person.HasIndex(x => x.Email);

        person.Property(x => x.PasswordHash).IsRequired();
        person.Property(x => x.Salt).IsRequired();

        person.Property(x => x.IsDeleted).HasDefaultValue(false);
        person.Property(x => x.EmailVerified).HasDefaultValue(false);

        person.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        person.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        // Role classifies Person (FR-RO-01). Roles are reference data, so deletion is restricted.
        person.HasOne(x => x.Role)
            .WithMany(r => r.Persons)
            .HasForeignKey(x => x.RoleId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
