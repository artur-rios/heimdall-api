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

        auditLog.Property(x => x.Action).IsRequired();
        auditLog.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        // ActorPersonId/ActorRole/TargetId are plain nullable columns — no FK, no required navigation.
    }
}
