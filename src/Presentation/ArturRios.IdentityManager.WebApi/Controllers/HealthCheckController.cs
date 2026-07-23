using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.IdentityManager.WebApi.Controllers;

[Route("[controller]")]
public class HealthCheckController : Controller
{
    [HttpGet]
    [Route("")]
    [AllowAnonymous]
    public ActionResult<DataOutput<string?>> HelloWorld()
    {
        var result = DataOutput<string?>.New
            .WithData("Hello world!")
            .WithMessage("Identity manager API is working.");

        return ResponseResolver.Resolve(result, HttpStatusCodes.Ok);
    }
}
