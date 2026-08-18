using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to read the caller's own two-factor authentication status (FR-2F-15). The person
///     acted on is always the caller: <see cref="ActingPersonId" />/<see cref="ActingRole" /> are
///     set by the controller from the authenticated caller and are never taken from the request,
///     which is what keeps a person's configuration reachable only through their own identity and
///     never by an identifier in a path (see <c>TwoFactorAuth</c>). The pagination members inherited
///     from <see cref="BaseQuery" /> are unused.
/// </summary>
public class GetTwoFactorStatusQuery : BaseQuery, IActorScoped
{
    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
