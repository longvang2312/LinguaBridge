using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Permissions;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace AppInHouse.Identity.Admin.Api.Controllers;

[Route($"{Root}/permissions")]
[SwaggerTag("Permission definitions (global or owned by a project)")]
public class PermissionsController : AdminControllerBase
{
    private readonly IPermissionAdminService _permissions;

    public PermissionsController(IPermissionAdminService permissions) => _permissions = permissions;

    [HttpGet]
    [SwaggerOperation(
        Summary = "Lists permissions",
        Description = "With projectId: the permissions that can apply to that project (its own and the global ones).")]
    [ProducesResponseType(typeof(PagedResult<PermissionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PermissionListQuery query, CancellationToken cancellationToken)
        => Ok(await _permissions.ListAsync(query, cancellationToken));

    [HttpPost]
    [SwaggerOperation(
        Summary = "Creates a permission",
        Description = "Global permissions: global administrators only. Project permissions: global administrators or administrators of that project.")]
    [ProducesResponseType(typeof(PermissionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreatePermissionRequest request, CancellationToken cancellationToken)
    {
        var permission = await _permissions.CreateAsync(request, cancellationToken);
        return Created($"{Root}/permissions/{permission.Id}", permission);
    }

    [HttpPut("{id:guid}")]
    [SwaggerOperation(Summary = "Updates a permission", Description = "The code and the project cannot be changed. Deactivating invalidates cached permissions.")]
    [ProducesResponseType(typeof(PermissionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePermissionRequest request, CancellationToken cancellationToken)
        => Ok(await _permissions.UpdateAsync(id, request, cancellationToken));
}
