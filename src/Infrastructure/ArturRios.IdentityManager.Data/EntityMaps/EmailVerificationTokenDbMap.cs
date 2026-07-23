using ArturRios.IdentityManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.IdentityManager.Data.EntityMaps;

internal static class EmailVerificationTokenDbMap
{
    public static void Configure(this EntityTypeBuilder<EmailVerificationToken> token)
    {
        // Internal bigint Id only; no PublicId — the caller-facing reference is the Token string (§4.0).
        token.HasKey(x => x.Id);

        token.Property(x => x.Token).IsRequired();
        token.HasIndex(x => x.Token).IsUnique();

        token.Property(x => x.ExpiresAt).IsRequired();
        token.Property(x => x.Used).HasDefaultValue(false);

        // Tokens are removed when their person is hard-deleted (UC-10).
        token.HasOne(x => x.Person)
            .WithMany(p => p.EmailVerificationTokens)
            .HasForeignKey(x => x.PersonId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
