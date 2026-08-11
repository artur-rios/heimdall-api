using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class TwoFactorRecoveryCodeDbMap
{
    public static void Configure(this EntityTypeBuilder<TwoFactorRecoveryCode> recoveryCode)
    {
        recoveryCode.ToTable("two_factor_recovery_code");

        // Internal bigint Id only; never addressed by ID — reached through the owning
        // TwoFactorAuth configuration.
        recoveryCode.HasKey(x => x.Id);

        recoveryCode.Property(x => x.CodeHash).IsRequired();

        recoveryCode.Property(x => x.Used).HasDefaultValue(false);
        recoveryCode.Property(x => x.UsedAt);

        recoveryCode.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        // Codes are removed when their two-factor configuration is removed.
        recoveryCode.HasOne(x => x.TwoFactorAuth)
            .WithMany(t => t.RecoveryCodes)
            .HasForeignKey(x => x.TwoFactorAuthId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
