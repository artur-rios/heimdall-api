using ArturRios.Heimdall.Query.Input;

namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>Input validation for <see cref="ListErasureRequestsQuery" /> (UC-43, NFR-10).</summary>
/// <remarks>
///     Page bounds only. The single filter is a <c>bool</c>, which the binder has already rejected
///     anything unusable for.
/// </remarks>
public class ListErasureRequestsQueryValidator : PaginatedQueryValidator<ListErasureRequestsQuery>;
