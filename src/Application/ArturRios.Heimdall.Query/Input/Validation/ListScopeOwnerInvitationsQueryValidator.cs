namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>Input validation for <see cref="ListScopeOwnerInvitationsQuery" /> (UC-58, NFR-10).</summary>
/// <remarks>Page bounds only: the listing has no filter.</remarks>
public class ListScopeOwnerInvitationsQueryValidator : PaginatedQueryValidator<ListScopeOwnerInvitationsQuery>;
