using AppInHouse.Identity.Application.Admin.Projects;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace AppInHouse.Identity.Admin.Api.Controllers;

[Route($"{Root}/projects/{{projectId:guid}}/users")]
[SwaggerTag("Project membership, roles and direct permissions of users")]
public class ProjectAssignmentsController : AdminControllerBase
{
    private readonly IProjectAssignmentService _assignments;

    public ProjectAssignmentsController(IProjectAssignmentService assignments) => _assignments = assignments;

    [HttpPost("assign")]
    [SwaggerOperation(
        Summary = "Assigns a user to the project",
        Description = "Global administrators or administrators of the project. Only a global administrator can set IsProjectAdmin.")]
    [ProducesResponseType(typeof(ProjectUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Assign(Guid projectId, [FromBody] AssignProjectUserRequest request, CancellationToken cancellationToken)
        => Ok(await _assignments.AssignUserAsync(projectId, request, cancellationToken));

    [HttpPost("{userId}/roles")]
    [SwaggerOperation(
        Summary = "Sets the roles of a user in the project",
        Description = "Replaces the complete set of roles. Invalidates the cached permissions of the user.")]
    [ProducesResponseType(typeof(UserRolesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetRoles(Guid projectId, string userId, [FromBody] SetUserRolesRequest request, CancellationToken cancellationToken)
        => Ok(await _assignments.SetUserRolesAsync(projectId, userId, request, cancellationToken));

    [HttpPost("{userId}/permissions")]
    [SwaggerOperation(
        Summary = "Sets the direct permissions of a user in the project",
        Description = "Replaces the complete set of direct grants and revocations (IsRevoked). Invalidates the cached permissions of the user.")]
    [ProducesResponseType(typeof(UserPermissionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetPermissions(Guid projectId, string userId, [FromBody] SetUserPermissionsRequest request, CancellationToken cancellationToken)
        => Ok(await _assignments.SetUserPermissionsAsync(projectId, userId, request, cancellationToken));
}
