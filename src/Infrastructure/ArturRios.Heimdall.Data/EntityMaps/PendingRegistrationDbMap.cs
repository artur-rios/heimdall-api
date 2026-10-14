using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class PendingRegistrationDbMap
{
    public static void Configure(this EntityTypeBuilder<PendingRegistration> pending)
    {
        pending.ToTable("pending_registration");

        // Internal bigint Id as the key. The registrant's reference is the mailed token; an
        // administrator's is PublicId (UC-49, UC-50; §4.0).
        pending.HasKey(x => x.Id);

        pending.Property(x => x.PublicId).IsRequired();
        pending.HasIndex(x => x.PublicId).IsUnique();

        // Name is bounded at 200 by the entity's [MaxLength]. Both are caller-supplied text stored before
        // any person exists, so the column matches the request's bound (PersonFieldRules).
        pending.Property(x => x.Name).IsRequired();
        pending.Property(x => x.Email).IsRequired().HasMaxLength(256);
        pending.Property(x => x.PasswordHash).IsRequired();
        pending.Property(x => x.Salt).IsRequired();
        pending.Property(x => x.TokenHash).IsRequired();
        pending.HasIndex(x => x.TokenHash).IsUnique();
        pending.Property(x => x.ExpiresAt).IsRequired();
        pending.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        pending.Property(x => x.FailedAttempts).HasDefaultValue(0);

        // A pending registration means nothing once its scope is gone (UC-05 hard delete).
        pending.HasOne(x => x.Scope)
            .WithMany()
            .HasForeignKey(x => x.ScopeId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
