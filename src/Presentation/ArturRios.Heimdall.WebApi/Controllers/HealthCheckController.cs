using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Heimdall.WebApi.Monitoring;
using ArturRios.Heimdall.WebApi.Security;
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
///     <c>EdgePipeline</c> rewrites to this address.
/// </remarks>
[Route("api/healthcheck")]
public class HealthCheckController(DatabaseHealthCheck databaseHealthCheck, ReadinessCache readinessCache) : Controller
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
    ///     Readiness check (#215): <c>200 OK</c> when the database answers, <c>503 Service Unavailable</c>
    ///     when it does not. Public — no authentication required.
    /// </summary>
    /// <remarks>
    ///     Liveness above reads no database, so an instance that has lost it stays "healthy" and keeps
    ///     receiving traffic it can only fail. This is the probe for that: an orchestrator or load
    ///     balancer stops routing to the instance without restarting it. It asks
    ///     <c>DatabaseHealthCheck</c> alone and answers with the bare status, never a service
    ///     breakdown or a cause, which is why it can be anonymous and reachable from the internet. The
    ///     breakdown is on the metrics port (<c>InternalHealthEndpoint</c>), and the cause in the log,
    ///     where <c>DatabaseHealthCheck</c> writes it.
    ///     The answer is held for five seconds (<c>ReadinessCache</c>), so an anonymous caller
    ///     cannot make the probe a database read per request.
    /// </remarks>
    [HttpGet]
    [Route("ready")]
    [AllowAnonymous]
    [SuppressServerErrorLog]
    public async Task<ActionResult<DataOutput<string?>>> Ready()
    {
        var healthy = await readinessCache.IsReadyAsync(databaseHealthCheck.IsHealthyAsync);

        var result = DataOutput<string?>.New
            .WithData(healthy ? HealthStatuses.Healthy : HealthStatuses.Unhealthy);

        return result.ToActionResult(healthy ? HttpStatusCodes.Ok : HttpStatusCodes.ServiceUnavailable);
    }
}
