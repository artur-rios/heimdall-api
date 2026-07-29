using ArturRios.IdentityManager.Domain.Entities;

namespace ArturRios.IdentityManager.Command.Services;

/// <summary>
///     Issues, persists, and dispatches an email-verification token for a person (UC-06 /
///     FR-EV-01/02). Shared by every Create Person path so token logic is not duplicated.
/// </summary>
public interface IEmailVerificationService
{
    Task IssueAndSendAsync(Person person);
}
