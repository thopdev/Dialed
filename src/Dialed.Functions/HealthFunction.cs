using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace Dialed.Functions;

/// <summary>Liveness check for the Function app, used by deploy smoke tests.</summary>
public sealed class HealthFunction
{
    [Function("Health")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest request)
        => new OkObjectResult("Healthy");
}
