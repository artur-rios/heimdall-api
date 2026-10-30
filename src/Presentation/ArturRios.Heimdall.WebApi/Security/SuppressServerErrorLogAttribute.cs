namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Marks an action whose 5xx answers <see cref="ServerErrorLoggingFilter" /> must not log (#215).
/// </summary>
/// <remarks>
///     For an action that already logs its own failure once per interval: the readiness probe's 503 is
///     written once per <c>ReadinessCache</c> window by the database check, and a request line for every
///     probe would turn that into one Warning per request.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SuppressServerErrorLogAttribute : Attribute;
