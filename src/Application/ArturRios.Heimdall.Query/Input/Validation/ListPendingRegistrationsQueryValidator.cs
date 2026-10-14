namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>Input validation for <see cref="ListPendingRegistrationsQuery" /> (UC-49, NFR-10).</summary>
/// <remarks>Page bounds only: the listing has no filter.</remarks>
public class ListPendingRegistrationsQueryValidator : PaginatedQueryValidator<ListPendingRegistrationsQuery>;
