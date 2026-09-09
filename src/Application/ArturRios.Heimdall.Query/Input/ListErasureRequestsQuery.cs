using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list the erasure requests that have not yet been carried out (UC-43), so an
///     administrator can see what is outstanding and what is overdue.
/// </summary>
/// <remarks>
///     The listing exists for one reason: GDPR Art. 12(3) and LGPD Art. 19 §2 set a deadline, and a
///     deadline nobody can see is one nobody meets. Most requests need no human at all — the
///     anonymisation pass carries them out — but the ones NFR-12 blocks need an owner transferred,
///     and without this they would sit unnoticed until the deadline had already passed.
/// </remarks>
public class ListErasureRequestsQuery : BaseQuery, IActorScoped
{
    /// <summary>
    ///     When true, only the requests whose deadline has already passed. The default is every
    ///     outstanding request, because a queue that shows only what is already late is a queue that
    ///     guarantees lateness.
    /// </summary>
    public bool OverdueOnly { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
