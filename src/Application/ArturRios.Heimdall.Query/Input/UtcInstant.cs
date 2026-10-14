namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Reads an instant bound from a query string as the UTC instant this API's contract says it is
///     (UC-48's <c>From</c>/<c>To</c>).
/// </summary>
/// <remarks>
///     ASP.NET Core binds <c>2026-10-14T00:00:00Z</c> as UTC but <c>2026-10-14T00:00:00</c>, with no
///     offset, as <see cref="DateTimeKind.Unspecified" />, and Npgsql refuses an unspecified
///     <see cref="DateTime" /> for a <c>timestamp with time zone</c> parameter. Without this an
///     offset-less filter — what an HTML <c>datetime-local</c> input sends — would answer 500.
/// </remarks>
public static class UtcInstant
{
    /// <summary>
    ///     <paramref name="value" /> as UTC: unchanged when it already is, converted when it is local,
    ///     and taken as UTC when it carries no kind at all.
    /// </summary>
    public static DateTime Of(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
