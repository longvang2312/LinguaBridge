using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Projects;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace AppInHouse.Identity.Admin.Api.Controllers;

[Route($"{Root}/projects")]
[SwaggerTag("Projects (tenants) that roles and permissions are scoped to")]
public class ProjectsController : AdminControllerBase
{
    private readonly IProjectAdminService _projects;

    public ProjectsController(IProjectAdminService projects) => _projects = projects;

    [HttpGet]
    [SwaggerOperation(Summary = "Lists projects", Description = "Global administrators see all projects, project administrators only theirs.")]
    [ProducesResponseType(typeof(PagedResult<ProjectDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] ProjectListQuery query, CancellationToken cancellationToken)
        => Ok(await _projects.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [SwaggerOperation(Summary = "Gets a project")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _projects.GetAsync(id, cancellationToken));

    [HttpPost]
    [SwaggerOperation(Summary = "Creates a project", Description = "Global administrators only.")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = await _projects.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = project.Id }, project);
    }

    [HttpPut("{id:guid}")]
    [SwaggerOperation(Summary = "Updates the name and description of a project", Description = "Global administrators or administrators of this project.")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProjectRequest request, CancellationToken cancellationToken)
        => Ok(await _projects.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/status")]
    [SwaggerOperation(Summary = "Locks or re-activates a project", Description = "Global administrators only. Invalidates the cached permissions of the project.")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetProjectStatusRequest request, CancellationToken cancellationToken)
        => Ok(await _projects.SetStatusAsync(id, request, cancellationToken));
}
