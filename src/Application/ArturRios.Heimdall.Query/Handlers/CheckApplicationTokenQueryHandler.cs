using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="CheckApplicationTokenQuery" /> (UC-57, FR-AP-15/16): validates the token
///     offline, then confirms against the database that its application and scope are live and
///     agree with it, and that the credential it was issued from is still live.
/// </summary>
/// <remarks>
///     This is the documented revocation policy: revoking a credential (UC-55), letting it expire, or
///     deleting the application or its scope takes effect here at once, for tokens already issued as
///     much as for new ones. A consumer re-checks before every sensitive release. Every refusal is
///     <see cref="ApplicationTokenMessages.TokenNotAccepted" />, with no detail.
/// </remarks>
public class CheckApplicationTokenQueryHandler(
    IApplicationTokenValidator validator,
    IAsyncReadOnlyRepository<ApplicationCredential, long> credentialReader)
    : IQueryHandlerAsync<CheckApplicationTokenQuery, ApplicationTokenCheckOutput>
{
    public async Task<DataOutput<ApplicationTokenCheckOutput?>> HandleAsync(
        CheckApplicationTokenQuery query, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<ApplicationTokenCheckOutput?>.New;

        var principal = await validator.ValidateAsync(query.Token);

        if (principal is null)
        {
            return output.WithError(ApplicationTokenMessages.TokenNotAccepted);
        }

        var now = DateTime.UtcNow;
        var check = await credentialReader.Query()
            .Where(ApplicationCredential.LiveAt(now))
            .Where(x => x.PublicId == principal.CredentialId &&
                        x.Application.PublicId == principal.ApplicationId &&
                        !x.Application.IsDeleted &&
                        x.Application.Scope.PublicId == principal.ScopeId &&
                        !x.Application.Scope.IsDeleted)
            .Select(x => new ApplicationTokenCheckOutput
            {
                ApplicationId = x.Application.PublicId,
                Name = x.Application.Name,
                ScopeId = x.Application.Scope.PublicId,
                CredentialId = x.PublicId,
                ExpiresAt = principal.ExpiresAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        return check is null
            ? output.WithError(ApplicationTokenMessages.TokenNotAccepted)
            : output.WithData(check).WithMessage(ApplicationTokenMessages.TokenAccepted);
    }
}
