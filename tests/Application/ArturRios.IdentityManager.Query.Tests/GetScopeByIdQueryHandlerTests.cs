namespace ArturRios.IdentityManager.Query.Tests;

// Unit tests for GetScopeByIdQueryHandler (UC-02).
// Cover the main flow (scope found) plus alternative flow AF-02a (scope not found), and the
// include-deleted behavior (FR-SC-07). AF-02b (not authorized) is a functional concern.
// See docs/Testing Specification Document.md §6 for the pattern (fake collaborators, assert on DataOutput).
public class GetScopeByIdQueryHandlerTests
{
}
