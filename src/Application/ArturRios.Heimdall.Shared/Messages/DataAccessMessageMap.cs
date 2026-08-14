using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     HTTP status codes for the failures the persistence layer classifies, folded into every use
///     case's own map so a repository failure answers with a status that describes it.
/// </summary>
/// <remarks>
///     <para>
///         These messages are not this application's. They come from
///         <see cref="RelationalErrors" />, which since ArturRios.Data.Relational.Core 4.0.0 maps a
///         provider exception onto one of a small set of fixed, caller-safe strings. Before that a
///         failure arrived as the provider's own text — a unique violation named the index and the
///         conflicting value, a truncation named the column and its type — so there was nothing
///         stable to key a status off, and every one of them fell through to the resolver's 400
///         default. A duplicate that lost a race was reported as a bad request, and so was a
///         database that had gone away.
///     </para>
///     <para>
///         Only the classified failures are listed. <see cref="RelationalErrors.GenericMessage" /> is
///         deliberately absent: it covers everything the library could not place, which spans both
///         causes the caller can fix (a value longer than its column, when a validator failed to
///         bound it) and causes only the operator can, so neither 400 nor 500 is right for all of it.
///         It keeps the 400 default, which is the safer of the two — a 500 would tell a caller their
///         request was blameless when it may not have been.
///     </para>
/// </remarks>
public static class DataAccessMessageMap
{
    private static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // A write lost a race against a unique index — the state the caller asked for conflicts with
        // state that already exists, which is exactly 409. The application checks these rules before
        // writing (FR-PE-09, FR-GO-07), so reaching here means two requests interleaved between the
        // check and the insert; the index is what actually holds the invariant.
        [RelationalErrors.UniqueViolationMessage] = HttpStatusCodes.Conflict,

        // A foreign key, NOT NULL, or CHECK the request would have broken. Also a conflict with
        // existing state rather than a malformed request.
        [RelationalErrors.IntegrityViolationMessage] = HttpStatusCodes.Conflict,

        // Someone else changed the row between the read and the write. The caller's request was
        // valid when they made it and may well succeed on a retry.
        [RelationalErrors.ConcurrencyMessage] = HttpStatusCodes.Conflict,

        // The database is unreachable or overloaded. 503 rather than 500: it says the condition is
        // temporary, which is the one thing a client needs to know to decide whether to retry.
        [RelationalErrors.TransientMessage] = HttpStatusCodes.ServiceUnavailable
    };

    /// <summary>
    ///     Combines a use case's own message-to-status map with the persistence failures above.
    /// </summary>
    /// <remarks>
    ///     The use case's entries win on a collision. None collide today — the library's messages are
    ///     phrased unlike any of this application's — but a use case owns its own vocabulary, and a
    ///     map assembled here should never be able to override it.
    /// </remarks>
    public static IReadOnlyDictionary<string, int> CombinedWith(IReadOnlyDictionary<string, int> useCaseStatusCodes)
    {
        var combined = new Dictionary<string, int>(StatusCodes);

        foreach (var (message, statusCode) in useCaseStatusCodes)
        {
            combined[message] = statusCode;
        }

        return combined;
    }
}
