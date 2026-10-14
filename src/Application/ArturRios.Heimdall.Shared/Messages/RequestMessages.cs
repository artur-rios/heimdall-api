namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages for failures that belong to no use case because they happen before one
///     starts (NFR-10: all inputs shall be validated before processing). Shared across every
///     endpoint, like <see cref="PaginationMessages" />, since the rule — an input that cannot be
///     bound is refused — does not vary by endpoint.
/// </summary>
public static class RequestMessages
{
    /// <summary>
    ///     The request could not be bound to the action's parameters: a body that is not JSON, or a
    ///     value of the wrong type in the body, query string or route. Deliberately says no more —
    ///     the framework's own binding errors would name properties and types of the internal model.
    /// </summary>
    public const string Malformed = "The request is malformed.";
}
