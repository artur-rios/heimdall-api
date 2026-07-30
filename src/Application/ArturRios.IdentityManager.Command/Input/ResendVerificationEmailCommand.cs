using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to reissue an email verification link for the caller's own address (UC-15, FR-EV-04).
///     Carries nothing but <see cref="ActingPersonId" />/<see cref="ActingRole" />, which the
///     controller sets from the authenticated caller and never binds from the request.
/// </summary>
/// <remarks>
///     The absence of any other field is the authorization rule. An email or a person id in the body
///     would be a way to ask for somebody else's verification link, and UC-15 describes a person
///     requesting their own.
/// </remarks>
public class ResendVerificationEmailCommand : BaseCommand, IActorScoped
{
    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
