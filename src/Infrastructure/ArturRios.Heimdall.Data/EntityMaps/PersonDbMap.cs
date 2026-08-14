using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class PersonDbMap
{
    public static void Configure(this EntityTypeBuilder<Person> person)
    {
        person.ToTable("person");

        person.HasKey(x => x.Id);

        person.Property(x => x.PublicId).IsRequired();
        person.HasIndex(x => x.PublicId).IsUnique();

        person.Property(x => x.Name).IsRequired();

        person.Property(x => x.Email).IsRequired();

        // Email uniqueness is conditional (FR-PE-09), and its two halves differ in whether a unique
        // index can express them at all.
        //
        // The admin half — unique system-wide across live ScopeAdmins and SystemAdmins — reads only
        // columns of this table, so it is a partial unique index over LOWER(email), created in raw
        // SQL by the AddPersonEmailUniqueness migration (EF models neither an expression index nor
        // one filtered on a role set). It is the real enforcement: the application's check-then-
        // insert cannot be, since two concurrent creates both read "free" before either writes.
        //
        // The User half — unique per scope — reads the scope from SCOPE_USER, so no single-table
        // index covers it, and a trigger would not close the race either (two concurrent inserts see
        // the same pre-write snapshot under READ COMMITTED). It stays enforced in the application
        // layer, and closing its race properly needs the scope denormalised onto a column this table
        // can index. See docs/content/en/docs/domain-model.md.
        //
        // This non-unique index remains for lookup speed: UC-11 and UC-12 both resolve a person by
        // address on every call.
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
