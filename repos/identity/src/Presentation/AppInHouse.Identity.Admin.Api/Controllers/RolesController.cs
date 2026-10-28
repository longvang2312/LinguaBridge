using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Roles;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace AppInHouse.Identity.Admin.Api.Controllers;

[Route($"{Root}/roles")]
[SwaggerTag("Roles and the permissions they carry")]
public class RolesController : AdminControllerBase
{
    private readonly IRoleAdminService _roles;

    public RolesController(IRoleAdminService roles) => _roles = roles;

    [HttpGet]
    [SwaggerOperation(
        Summary = "Lists roles",
        Description = "Administrators can list roles; the permissions of each role are only included for global administrators.")]
    [ProducesResponseType(typeof(PagedResult<RoleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] RoleListQuery query, CancellationToken cancellationToken)
        => Ok(await _roles.ListAsync(query, cancellationToken));

    [HttpPost]
    [SwaggerOperation(Summary = "Creates a role", Description = "Global administrators only.")]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateRoleRequest request, CancellationToken cancellationToken)
    {
        var role = await _roles.CreateAsync(request, cancellationToken);
        return Created($"{Root}/roles/{role.Id}", role);
    }

    [HttpPut("{id}")]
    [SwaggerOperation(Summary = "Updates a role", Description = "Global administrators only. The GlobalAdmin role cannot be renamed.")]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateRoleRequest request, CancellationToken cancellationToken)
        => Ok(await _roles.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id}/permissions")]
    [SwaggerOperation(
        Summary = "Sets the permissions of a role",
        Description = "Replaces the complete set. Global administrators only. Invalidates every cached permission set.")]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetPermissions(string id, [FromBody] SetRolePermissionsRequest request, CancellationToken cancellationToken)
        => Ok(await _roles.SetPermissionsAsync(id, request, cancellationToken));
}
