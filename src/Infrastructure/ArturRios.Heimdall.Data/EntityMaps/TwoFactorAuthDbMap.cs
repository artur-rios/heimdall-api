using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class TwoFactorAuthDbMap
{
    public static void Configure(this EntityTypeBuilder<TwoFactorAuth> twoFactorAuth)
    {
        twoFactorAuth.ToTable("two_factor_auth");

        // Internal bigint Id only; a person's own configuration is reached through their
        // authenticated identity, never addressed by ID in a path (§4.0).
        twoFactorAuth.HasKey(x => x.Id);

        twoFactorAuth.Property(x => x.AppEnabled).HasDefaultValue(false);
        twoFactorAuth.Property(x => x.EmailEnabled).HasDefaultValue(false);

        twoFactorAuth.Property(x => x.TotpSecretEncrypted);

        twoFactorAuth.Property(x => x.IsActive).HasDefaultValue(false);

        twoFactorAuth.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        twoFactorAuth.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        // At most one configuration per person; hard-deleting the person cascades to it (UC-10).
        twoFactorAuth.HasIndex(x => x.PersonId).IsUnique();
        twoFactorAuth.HasOne(x => x.Person)
            .WithOne(p => p.TwoFactorAuth)
            .HasForeignKey<TwoFactorAuth>(x => x.PersonId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
