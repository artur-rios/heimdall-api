namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>Input validation for <see cref="ListMyScopeInvitationsQuery" /> (UC-59, NFR-10).</summary>
/// <remarks>Page bounds only: the listing has no filter.</remarks>
public class ListMyScopeInvitationsQueryValidator : PaginatedQueryValidator<ListMyScopeInvitationsQuery>;
