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
        // The User half — unique per scope — is now a partial unique index too, created by the
        // AddPersonScopeId migration over (scope_id, LOWER(email)) WHERE role_id = 3 AND
        // is_deleted = false. It reads the scope from Person.ScopeId, the copy of SCOPE_USER's
        // ScopeId that exists for exactly this purpose: the relationship itself is still SCOPE_USER,
        // but an index has to be written over one table's columns, and every other column the rule
        // needs already lives here. The condition matches the application's check term for term, so
        // the rule did not change — only who is able to enforce it under concurrency.
        //
        // This non-unique index remains for lookup speed: UC-11 and UC-12 both resolve a person by
        // address on every call.
        person.HasIndex(x => x.Email);

        person.Property(x => x.PasswordHash).IsRequired();
        person.Property(x => x.Salt).IsRequired();

        person.Property(x => x.IsDeleted).HasDefaultValue(false);
        person.Property(x => x.EmailVerified).HasDefaultValue(false);

        // Deliberately not a foreign key and not a navigation. It carries no relationship of its own
        // — SCOPE_USER is the relationship, and mapping this as a second one would give EF two ways
        // to describe the same association and two chances to disagree. It is a value the per-scope
        // uniqueness index is written over, kept in step by the handlers that write the membership.
        person.Property(x => x.ScopeId);

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
