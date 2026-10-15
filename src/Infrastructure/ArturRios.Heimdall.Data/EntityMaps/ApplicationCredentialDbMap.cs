using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class ApplicationCredentialDbMap
{
    public static void Configure(this EntityTypeBuilder<ApplicationCredential> credential)
    {
        credential.ToTable("application_credential");

        credential.HasKey(x => x.Id);

        credential.Property(x => x.PublicId).IsRequired();
        credential.HasIndex(x => x.PublicId).IsUnique();

        // UC-56 looks a credential up by the hash of the presented secret, so the hash is indexed;
        // unique because two credentials sharing one would make that lookup ambiguous.
        credential.Property(x => x.SecretHash).IsRequired();
        credential.HasIndex(x => x.SecretHash).IsUnique();

        credential.Property(x => x.CreatedAt).IsRequired();

        // Credentials go with their application when it is hard-deleted (UC-20); a logical delete
        // leaves them, and UC-56/UC-57 refuse them through the application's flag.
        credential.HasOne(x => x.Application)
            .WithMany(a => a.Credentials)
            .HasForeignKey(x => x.ApplicationId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
