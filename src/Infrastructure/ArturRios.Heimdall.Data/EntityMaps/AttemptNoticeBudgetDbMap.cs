using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class AttemptNoticeBudgetDbMap
{
    public static void Configure(this EntityTypeBuilder<AttemptNoticeBudget> budget)
    {
        budget.ToTable("attempt_notice_budget");

        // Internal bigint Id, never exposed (NFR-15): the row is reached by its address digest only.
        budget.HasKey(x => x.Id);

        // One row per address. The charge's INSERT … ON CONFLICT (address_hash) relies on this index.
        budget.Property(x => x.AddressHash).IsRequired().HasMaxLength(64);
        budget.HasIndex(x => x.AddressHash).IsUnique();

        budget.Property(x => x.NoticesSent).IsRequired();
        budget.Property(x => x.WindowEndsAt).IsRequired();

        // The purge reads budgets whose window ended before the grace period.
        budget.HasIndex(x => x.WindowEndsAt);
    }
}
