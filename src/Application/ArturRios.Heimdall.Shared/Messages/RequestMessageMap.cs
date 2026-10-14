using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>Status codes for <see cref="RequestMessages" /> (NFR-10, #186).</summary>
public static class RequestMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // NFR-10 — an input that cannot be bound is refused, never executed with a default.
        [RequestMessages.Malformed] = HttpStatusCodes.BadRequest
    };
}
