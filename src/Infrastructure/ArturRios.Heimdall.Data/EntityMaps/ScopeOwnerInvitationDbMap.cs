using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class ScopeOwnerInvitationDbMap
{
    public static void Configure(this EntityTypeBuilder<ScopeOwnerInvitation> invitation)
    {
        // An outcome is one of the four, and only an ended row has one; an ended row without one ended by
        // expiry (ScopeOwnerInvitationOutcomes).
        invitation.ToTable("scope_owner_invitation", table =>
        {
            table.HasCheckConstraint("ck_scope_owner_invitation_outcome_known", "outcome IS NULL OR outcome BETWEEN 1 AND 4");
            table.HasCheckConstraint("ck_scope_owner_invitation_outcome_ends_it", "outcome IS NULL OR ended_at IS NOT NULL");
        });

        // Internal bigint Id as the key; PublicId is the reference the API uses (§4.0).
        invitation.HasKey(x => x.Id);

        invitation.Property(x => x.PublicId).IsRequired();
        invitation.HasIndex(x => x.PublicId).IsUnique();

        invitation.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        invitation.Property(x => x.ExpiresAt).IsRequired();
        invitation.Property(x => x.Outcome).HasColumnType("smallint");

        // One unended invitation per scope and invitee (FR-SC-18). An expired row still holds the slot until
        // ScopeOwnerInvitations.OpenAsync closes it.
        invitation.HasIndex(x => new { x.ScopeId, x.InviteePersonId })
            .IsUnique()
            .HasFilter("ended_at IS NULL");

        // An invitation means nothing once its scope or its invitee is gone (UC-05, UC-10).
        invitation.HasOne(x => x.Scope)
            .WithMany()
            .HasForeignKey(x => x.ScopeId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        invitation.HasOne(x => x.Invitee)
            .WithMany()
            .HasForeignKey(x => x.InviteePersonId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // The inviter's removal does not end it: the invitation stands for the scope, not for them.
        invitation.HasOne(x => x.InvitedBy)
            .WithMany()
            .HasForeignKey(x => x.InvitedByPersonId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
