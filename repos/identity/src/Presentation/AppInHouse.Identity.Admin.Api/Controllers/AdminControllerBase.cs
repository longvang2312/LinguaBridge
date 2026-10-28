using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.Identity.Admin.Api.Controllers;

/// <summary>
/// Base of every admin controller: authentication is mandatory (deny by default), responses are JSON and the
/// common failures are documented once. What a caller may actually do is decided by the use cases.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
public abstract class AdminControllerBase : ControllerBase
{
    public const string Root = "api/v1";

    /// <summary>Responses that carry a secret must never be cached.</summary>
    protected void NoStore() => Response.Headers.CacheControl = "no-store";
}
