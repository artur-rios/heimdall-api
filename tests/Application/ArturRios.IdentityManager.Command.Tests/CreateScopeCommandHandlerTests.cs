namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for CreateScopeCommandHandler (UC-01).
// Cover the main flow plus alternative flows AF-01a (name exists), AF-01b (invalid input / no owner),
// and AF-01d (owner is not a valid ScopeAdmin). AF-01c (not System Admin) is a functional concern.
// See docs/Testing Specification Document.md §6 for the pattern (fake collaborators, assert on DataOutput).
public class CreateScopeCommandHandlerTests
{
}
