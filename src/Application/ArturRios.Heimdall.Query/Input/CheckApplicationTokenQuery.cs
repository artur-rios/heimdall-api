using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Checks an application token live (UC-57, FR-AP-15). <see cref="Token" /> is the bearer token
///     the controller read from the <c>Authorization</c> header, or <c>null</c> when there was none.
/// </summary>
public class CheckApplicationTokenQuery : BaseQuery
{
    public string? Token { get; set; }
}
