using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class PasswordResetTokenDbMap
{
    public static void Configure(this EntityTypeBuilder<PasswordResetToken> token)
    {
        token.ToTable("password_reset_token");

        // Internal bigint Id only; no PublicId — the caller-facing reference is the Token string (§4.0).
        token.HasKey(x => x.Id);

        token.Property(x => x.TokenHash).IsRequired();
        token.HasIndex(x => x.TokenHash).IsUnique();

        token.Property(x => x.ExpiresAt).IsRequired();
        token.Property(x => x.Used).HasDefaultValue(false);

        // Tokens are removed when their person is hard-deleted (UC-10).
        token.HasOne(x => x.Person)
            .WithMany(p => p.PasswordResetTokens)
            .HasForeignKey(x => x.PersonId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
