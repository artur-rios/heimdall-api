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

        // NFR-20's retention window is measured from DeletedAt, so the anonymisation pass reads it
        // on every run: indexed, and filtered to the rows that can possibly be due, which is a small
        // fraction of the table.
        googleUser.Property(x => x.DeletedAt);
        googleUser.Property(x => x.DeletionKind);
        googleUser.Property(x => x.AnonymisedAt);

        // NFR-23: the basis is required and defaults to Unrecorded, so a row can never be silent
        // about it — a null would be indistinguishable from "nobody has looked at this yet".
        googleUser.Property(x => x.LegalBasis).HasDefaultValue(0);
        googleUser.Property(x => x.PrivacyNoticeVersion);
        googleUser.Property(x => x.BasisRecordedAt);

        // NFR-24: indexed because every listing filters on it, and the restricted set is a small
        // fraction of the table.
        googleUser.Property(x => x.ProcessingRestrictedAt);
        googleUser.Property(x => x.RestrictionGround);
        googleUser.Property(x => x.RestrictionLiftNotifiedAt);

        googleUser.HasIndex(x => x.ProcessingRestrictedAt)
            .HasFilter("processing_restricted_at IS NOT NULL");

        googleUser.HasIndex(x => x.DeletedAt)
            .HasFilter("is_deleted = true AND anonymised_at IS NULL");

        googleUser.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        googleUser.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        // GoogleId (Google's 'sub' claim) is unique within the scope (FR-GO-08).
        googleUser.HasIndex(x => new { x.ScopeId, x.GoogleId }).IsUnique();

        // Email is unique within the scope among Google Users (FR-GO-07), case-insensitively — the
        // application compares addresses with LOWER(), so an index over the raw column would accept
        // a pair the handler had already refused and enforce a rule nobody wrote. The expression
        // form is applied in the migration's raw SQL: EF cannot model an index over an expression,
        // so this call reserves the name and the migration replaces its definition.
        //
        // The joint uniqueness with User persons' emails spans two tables and cannot be a unique
        // index at all; it stays in the application layer (CreateUserCommandHandler,
        // UpdatePersonCommandHandler, GoogleSignInCommandHandler).
        googleUser.HasIndex(x => new { x.ScopeId, x.Email })
            .IsUnique()
            .HasDatabaseName("ix_google_user_scope_id_email");

        // A Google User belongs to exactly one scope (FR-GO-06); hard-deleting the scope cascades
        // to its Google Users (NFR-14).
        googleUser.HasOne(x => x.Scope)
            .WithMany(s => s.GoogleUsers)
            .HasForeignKey(x => x.ScopeId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
