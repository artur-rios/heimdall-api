using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Heimdall.WebApi.Controllers;

/// <remarks>
///     Under <c>api/</c> like every other route, because that prefix is all that carries a request on
///     the web UI's host through to the API — see <c>OpenApiContractTests</c>. The container probe and
///     yggdrasil's status catalog call the root <c>/healthcheck</c>, which the edge pipeline in
///     <see cref="Startup" /> rewrites to this address.
/// </remarks>
[Route("api/healthcheck")]
public class HealthCheckController(QueryMediator queryMediator) : Controller
{
    /// <summary>
    ///     Basic liveness check (UC-30, FR-HC-01): confirms the API process is up and responding.
    ///     Public — no authentication required.
    /// </summary>
    [HttpGet]
    [Route("")]
    [AllowAnonymous]
    public ActionResult<DataOutput<string?>> HelloWorld()
    {
        var result = DataOutput<string?>.New
            .WithData("Hello world!")
            .WithMessage("Heimdall API is working.");

        return result.ToActionResult(HttpStatusCodes.Ok);
    }

    /// <summary>
    ///     Detailed health check (UC-30, FR-HC-02…07): reports the status of each verified service
    ///     plus an aggregate general status. Restricted to System Admins (AF-30a → 403, AF-30b → 401).
    ///     Returns <c>200 OK</c> when healthy and <c>503 Service Unavailable</c> when any verified
    ///     service is down (FR-HC-07, AF-30c).
    /// </summary>
    [HttpGet]
    [Route("detailed")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<HealthCheckOutput?>>> Detailed()
    {
        var result = await queryMediator
            .ExecuteQueryAsync<DetailedHealthQuery, HealthCheckOutput>(new DetailedHealthQuery());

        var statusCode = result.Data?.Status == HealthStatuses.Healthy
            ? HttpStatusCodes.Ok
            : HttpStatusCodes.ServiceUnavailable;

        return result.ToActionResult(statusCode);
    }
}
