using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Re-applies erasure to identities that came back in a database restore (NFR-25).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this has to exist at all.</b> An erasure that the next restore silently undoes is
///         not an erasure. Backups are full copies and are never edited — editing one destroys the
///         integrity that is its purpose — so a restore reinstates whatever the database held when
///         the backup was taken, erased people included.
///     </para>
///     <para>
///         <b>Why the scheduled pass is not enough on its own.</b> Where the backup was taken
///         <em>after</em> the subject asked, the restored row still carries its deletion and
///         deadline, and NFR-20's pass completes the erasure again by itself. Where the backup
///         predates the request, the row comes back with no trace of it ever having been made, and
///         nothing inside the database knows to act. This is the only case that needs a list from
///         outside, and it is the case this command exists for.
///     </para>
/// </remarks>
public class ReapplyErasuresCommand : BaseCommand, IActorScoped
{
    /// <summary>
    ///     Public identifiers of the identities to erase, taken from the erasure ledger the restore
    ///     runbook keeps outside the database.
    /// </summary>
    public IEnumerable<Guid> SubjectIds { get; set; } = [];

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
