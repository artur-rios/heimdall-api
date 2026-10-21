using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class AuditLogDbMap
{
    public static void Configure(this EntityTypeBuilder<AuditLog> auditLog)
    {
        auditLog.ToTable("audit_log");
        auditLog.HasKey(x => x.Id);

        auditLog.Property(x => x.PublicId).IsRequired();
        auditLog.HasIndex(x => x.PublicId).IsUnique();
        auditLog.HasIndex(x => x.CreatedAt);
        auditLog.HasIndex(x => x.ActorPersonId);

        // UC-48 filters by target ("what happened to this entity?"); without this that is a scan of
        // a table that only grows.
        auditLog.HasIndex(x => x.TargetId);

        // NFR-21's pass selects the entries past the attribution period oldest first, and only those that
        // still have an actor, or a target, to clear (#206). Partial, so each holds only the rows its query
        // can still return and shrinks as the pass clears them; created with CONCURRENTLY by
        // AddAuditRetentionIndexes, since audit_log is written on every command.
        auditLog.HasIndex(x => x.CreatedAt, "ix_audit_log_created_at_attributed")
            .HasDatabaseName("ix_audit_log_created_at_attributed")
            .HasFilter("actor_person_id IS NOT NULL");
        auditLog.HasIndex(x => x.CreatedAt, "ix_audit_log_created_at_targeted")
            .HasDatabaseName("ix_audit_log_created_at_targeted")
            .HasFilter("target_id IS NOT NULL");

        auditLog.Property(x => x.Action).IsRequired();
        auditLog.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        // ActorPersonId/ActorRole/TargetId are plain nullable columns — no FK, no required navigation.
    }
}
