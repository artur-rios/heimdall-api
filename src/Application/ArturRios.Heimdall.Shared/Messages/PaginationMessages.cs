namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages for the pagination/filter validation shared by every paginated list query
///     (NFR-10: all inputs shall be validated before processing). Reused across every entity's list
///     query rather than duplicated per entity, since the rule itself — page number at least 1, page
///     size within a bounded range, filter strings within the length of the column they search — does
///     not vary by entity.
/// </summary>
public static class PaginationMessages
{
    /// <summary><c>PageNumber</c> was less than 1.</summary>
    public const string InvalidPageNumber = "Page number must be at least 1.";

    /// <summary>
    ///     <c>PageSize</c> was less than 1 or greater than the maximum allowed page size. The upper
    ///     bound exists so a caller cannot force an unbounded query merely by asking for it — the
    ///     underlying pagination library would otherwise happily hand back everything in one page.
    /// </summary>
    public const string InvalidPageSize = "Page size must be between 1 and 100.";

    /// <summary>
    ///     A free-text filter (name, email, etc.) exceeded the length of the column it searches — such
    ///     a filter could never match a row, so it is rejected as malformed rather than silently
    ///     executed against the database.
    /// </summary>
    public const string FilterTooLong = "Filter value is longer than the field it searches.";
}
