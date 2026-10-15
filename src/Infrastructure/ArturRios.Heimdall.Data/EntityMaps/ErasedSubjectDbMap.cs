using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class ErasedSubjectDbMap
{
    public static void Configure(this EntityTypeBuilder<ErasedSubject> erasedSubject)
    {
        erasedSubject.ToTable("erased_subject");

        // The key is the removed identity's PublicId (#195): the trail's target_id is looked up here
        // directly, and an id can be tombstoned only once. Never generated — it is copied from the
        // row being deleted.
        erasedSubject.HasKey(x => x.Id);
        erasedSubject.Property(x => x.Id).HasColumnName("public_id").ValueGeneratedNever();

        erasedSubject.Property(x => x.Kind).IsRequired();
        erasedSubject.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        // The pass's cleanup reads tombstones older than the grace period.
        erasedSubject.HasIndex(x => x.CreatedAt);

        // No foreign key to person or google_user: the row it names is about to be deleted, and the
        // tombstone has to outlive it.
    }
}
