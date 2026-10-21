using ArturRios.Heimdall.WebApi.Documentation;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ArturRios.Heimdall.WebApi.Controllers;

/// <summary>
///     The public halves of Heimdall's signing keys (NFR-31, Threat Model TH-43): the key set every
///     consumer verifies Heimdall's ES256 tokens with.
/// </summary>
/// <remarks>
///     Outside <c>api/</c>, at the path RFC 8615 reserves for it, because consumers fetch the key set
///     from the API's own host or the edge network, never through the web UI's host — see
///     <c>OpenApiContractTests</c>.
/// </remarks>
[Route(".well-known")]
public class SigningKeysController(TokenSigningKeys keys) : Controller
{
    /// <summary>
    ///     The public keys Heimdall's tokens are signed with, as an RFC 7517 JSON Web Key Set: the
    ///     current key first, then the previous one while a rotation is in progress. Public — no
    ///     authentication required, and any bearer token on the request is ignored.
    /// </summary>
    /// <remarks>
    ///     The body is the key set itself, not the <c>DataOutput</c> envelope every other endpoint
    ///     answers, so a standard JWKS reader can consume it unchanged. It carries no private member.
    ///     Cacheable for five minutes. Limited to ten requests a minute per address, a budget shared
    ///     with the other anonymous credential endpoints; a rejection answers 429 "Too many requests."
    ///     with a <c>Retry-After</c> header.
    /// </remarks>
    [HttpGet("jwks.json")]
    [AllowAnonymous]
    [EnableRateLimiting(Startup.AuthEndpointRateLimitPolicy)]
    [ProducesResponseType<JsonWebKeySetDocument>(StatusCodes.Status200OK, "application/json")]
    public ContentResult KeySet()
    {
        Response.Headers.CacheControl = "public, max-age=300";

        return Content(keys.JwksJson, "application/json");
    }
}
