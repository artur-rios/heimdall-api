using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class GoogleIdTokenUseDbMap
{
    public static void Configure(this EntityTypeBuilder<GoogleIdTokenUse> use)
    {
        use.ToTable("google_id_token_use");

        // FR-GO-21: the token's key is the primary key, so recording a use and finding an earlier one
        // are the same INSERT … ON CONFLICT DO NOTHING (AtomicWrites.TryRecordGoogleIdTokenUseAsync).
        use.HasKey(x => x.Id);
        use.Property(x => x.Id).HasColumnName("token_hash").HasMaxLength(64).ValueGeneratedNever();

        // The purge selects on it (NFR-19).
        use.Property(x => x.ExpiresAt).IsRequired();
        use.HasIndex(x => x.ExpiresAt);
    }
}
