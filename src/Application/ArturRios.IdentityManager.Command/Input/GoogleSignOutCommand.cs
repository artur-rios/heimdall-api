using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to end the caller's own Google-authenticated session (UC-26, FR-GO-18). Carries nothing
///     but <see cref="ActingPersonId" />/<see cref="ActingRole" />, which the controller sets from the
///     authenticated caller and never binds from the request.
/// </summary>
/// <remarks>
///     The absence of any other field is the authorization rule, as it is for
///     <see cref="ResendVerificationEmailCommand" />: a Google User id in the body would be a way to
///     sign somebody else out, and UC-26 describes a Google User ending their own session.
///     <see cref="ActingPersonId" /> holds the Google User's <c>PublicId</c> here — UC-25 issues the
///     token claiming it in the same position a person's would occupy.
/// </remarks>
public class GoogleSignOutCommand : BaseCommand, IActorScoped
{
    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
