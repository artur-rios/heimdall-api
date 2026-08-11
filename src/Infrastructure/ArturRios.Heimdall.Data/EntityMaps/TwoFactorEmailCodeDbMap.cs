using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class TwoFactorEmailCodeDbMap
{
    public static void Configure(this EntityTypeBuilder<TwoFactorEmailCode> emailCode)
    {
        emailCode.ToTable("two_factor_email_code");

        // Internal bigint Id only; never addressed by ID — reached through the owning
        // TwoFactorAuth configuration.
        emailCode.HasKey(x => x.Id);

        emailCode.Property(x => x.CodeHash).IsRequired();
        emailCode.Property(x => x.Salt).IsRequired();

        emailCode.Property(x => x.ExpiresAt).IsRequired();
        emailCode.Property(x => x.Used).HasDefaultValue(false);

        emailCode.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        // Codes are removed when their two-factor configuration is removed.
        emailCode.HasOne(x => x.TwoFactorAuth)
            .WithMany(t => t.EmailCodes)
            .HasForeignKey(x => x.TwoFactorAuthId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
