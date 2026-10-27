using AppInHouse.Identity.ClientSdk;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.Identity.IntegrationTests.Authorization;

/// <summary>Stands in for a child API (ERP, AIChat, ...) protected with the ClientSdk.</summary>
[ApiController]
public class SampleOrdersController : ControllerBase
{
    // Project from the X-Project-Code header or from the route.
    [HttpPost("api/orders")]
    [HttpPost("api/{projectCode}/orders")]
    [HasPermission("orders.create")]
    public IActionResult Create() => Ok(new { created = true });

    [HttpGet("api/orders")]
    [HasPermission("orders.view")]
    public IActionResult List() => Ok(new { items = Array.Empty<string>() });

    // Two attributes: both permissions are required.
    [HttpDelete("api/orders")]
    [HasPermission("orders.view")]
    [HasPermission("orders.delete")]
    public IActionResult Delete() => NoContent();
}
