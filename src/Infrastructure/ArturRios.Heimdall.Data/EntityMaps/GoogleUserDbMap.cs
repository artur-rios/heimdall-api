using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class GoogleUserDbMap
{
    public static void Configure(this EntityTypeBuilder<GoogleUser> googleUser)
    {
        googleUser.ToTable("google_user");

        googleUser.HasKey(x => x.Id);

        googleUser.Property(x => x.PublicId).IsRequired();
        googleUser.HasIndex(x => x.PublicId).IsUnique();

        googleUser.Property(x => x.GoogleId).IsRequired();

        googleUser.Property(x => x.Email).IsRequired();

        googleUser.Property(x => x.EmailVerified).HasDefaultValue(false);
        googleUser.Property(x => x.IsDeleted).HasDefaultValue(false);

        googleUser.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        googleUser.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        // GoogleId (Google's 'sub' claim) is unique within the scope (FR-GO-08).
        googleUser.HasIndex(x => new { x.ScopeId, x.GoogleId }).IsUnique();

        // Email is unique within the scope among Google Users (FR-GO-07). The joint uniqueness with
        // User persons' emails spans two tables and is enforced in the application layer.
        googleUser.HasIndex(x => new { x.ScopeId, x.Email }).IsUnique();

        // A Google User belongs to exactly one scope (FR-GO-06); hard-deleting the scope cascades
        // to its Google Users (NFR-14).
        googleUser.HasOne(x => x.Scope)
            .WithMany(s => s.GoogleUsers)
            .HasForeignKey(x => x.ScopeId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
