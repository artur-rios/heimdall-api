using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request for the detailed health check (UC-30, FR-HC-02). Carries no parameters — the response
///     is derived entirely from the registered service checks. The pagination members inherited from
///     <see cref="BaseQuery" /> are unused.
/// </summary>
public class DetailedHealthQuery : BaseQuery;
