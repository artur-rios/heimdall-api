using ArturRios.IdentityManager.Domain.Entities;

namespace ArturRios.IdentityManager.Command.Services;

/// <summary>
///     Issues a password reset token for a person and has it delivered (UC-12 steps 3 and 4,
///     FR-PR-02).
/// </summary>
public interface IPasswordResetService
{
    Task IssueAndSendAsync(Person person);
}
