using AppInHouse.Identity.Admin.Web.Infrastructure;
using AppInHouse.Identity.Admin.Web.ViewModels;
using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Permissions;
using AppInHouse.Identity.Application.Admin.Projects;
using AppInHouse.Identity.Application.Admin.Roles;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Application.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.Identity.Admin.Web.Controllers;

public class ProjectsController : PortalController
{
    private readonly IProjectAdminService _projects;
    private readonly IProjectAssignmentService _assignments;
    private readonly IRoleAdminService _roles;
    private readonly IPermissionAdminService _permissions;
    private readonly IAdminAuthorizer _authorizer;

    public ProjectsController(
        IProjectAdminService projects,
        IProjectAssignmentService assignments,
        IRoleAdminService roles,
        IPermissionAdminService permissions,
        IAdminAuthorizer authorizer)
    {
        _projects = projects;
        _assignments = assignments;
        _roles = roles;
        _permissions = permissions;
        _authorizer = authorizer;
    }

    public async Task<IActionResult> Index(ProjectListQuery query, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            query = new ProjectListQuery();

        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        var result = await _projects.ListAsync(query, cancellationToken);
        return View(new ProjectListViewModel(query, result.Items, PagerModel.From(result), caller.IsGlobalAdmin));
    }

    // ---- create / edit / status ----

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        // Only a global administrator can create projects; do not offer the form to anybody else.
        (await _authorizer.GetCallerAsync(cancellationToken)).RequireGlobalAdmin();
        return View(new CreateProjectRequest());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return View(request);

        var project = await _projects.CreateAsync(request, cancellationToken);
        Success($"Project {project.ProjectCode} was created.");
        return RedirectToAction(nameof(Details), new { id = project.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var project = await _projects.GetAsync(id, cancellationToken);
        return View(new ProjectEditViewModel
        {
            Id = project.Id,
            ProjectCode = project.ProjectCode,
            Form = new UpdateProjectRequest { Name = project.Name, Description = project.Description, ConcurrencyToken = project.ConcurrencyToken },
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, [Bind(Prefix = "Form")] UpdateProjectRequest form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var project = await _projects.GetAsync(id, cancellationToken);
            return View(new ProjectEditViewModel { Id = id, ProjectCode = project.ProjectCode, Form = form });
        }

        var updated = await _projects.UpdateAsync(id, form, cancellationToken);
        Success($"Project {updated.ProjectCode} was updated.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        var project = await _projects.SetStatusAsync(id, new SetProjectStatusRequest { IsActive = isActive }, cancellationToken);
        Success($"Project {project.ProjectCode} is now {(project.IsActive ? "active" : "locked")}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---- members ----

    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        var project = await _projects.GetAsync(id, cancellationToken);
        var members = await _assignments.ListMembersAsync(id, cancellationToken);
        var roles = await AllAsync(page => _roles.ListAsync(new RoleListQuery { Page = page, PageSize = 100 }, cancellationToken));

        return View(new ProjectDetailsViewModel(
            project, members, roles.ToDictionary(r => r.Id, r => r.Name), caller.IsGlobalAdmin));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignUser(Guid id, AssignProjectUserRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Failure("A user id is required.");
            return RedirectToAction(nameof(Details), new { id });
        }

        var member = await _assignments.AssignUserAsync(id, request, cancellationToken);
        Success($"User {member.UserId} is assigned to the project.");
        return RedirectToAction(nameof(Member), new { id, userId = member.UserId });
    }

    /// <summary>Roles and direct permissions of one member of the project.</summary>
    public async Task<IActionResult> Member(Guid id, string userId, CancellationToken cancellationToken)
    {
        var project = await _projects.GetAsync(id, cancellationToken);
        var members = await _assignments.ListMembersAsync(id, cancellationToken);
        var member = members.FirstOrDefault(m => m.UserId == userId)
            ?? throw new NotFoundException("The user is not a member of the project.");

        var roles = (await AllAsync(page => _roles.ListAsync(new RoleListQuery { Page = page, PageSize = 100 }, cancellationToken)))
            .Where(r => !string.Equals(r.Name, AuthorizationConstants.GlobalAdminRole, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var permissions = await AllAsync(page => _permissions.ListAsync(
            new PermissionListQuery { ProjectId = id, IsActive = true, Page = page, PageSize = 100 }, cancellationToken));

        return View(new MemberAccessViewModel(project, member, roles, permissions));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRoles(Guid id, string userId, List<string>? roleIds, CancellationToken cancellationToken)
    {
        await _assignments.SetUserRolesAsync(id, userId, new SetUserRolesRequest { RoleIds = roleIds ?? new List<string>() }, cancellationToken);
        Success("The roles of the user were updated.");
        return RedirectToAction(nameof(Member), new { id, userId });
    }

    /// <param name="states">Per permission id: <c>grant</c>, <c>revoke</c> or anything else for no direct entry.</param>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPermissions(Guid id, string userId, Dictionary<Guid, string>? states, CancellationToken cancellationToken)
    {
        var request = new SetUserPermissionsRequest();
        foreach (var (permissionId, state) in states ?? new Dictionary<Guid, string>())
        {
            if (state == "grant")
                request.Assignments.Add(new DirectPermissionItem { PermissionId = permissionId, IsRevoked = false });
            else if (state == "revoke")
                request.Assignments.Add(new DirectPermissionItem { PermissionId = permissionId, IsRevoked = true });
        }

        await _assignments.SetUserPermissionsAsync(id, userId, request, cancellationToken);
        Success("The direct permissions of the user were updated.");
        return RedirectToAction(nameof(Member), new { id, userId });
    }
}
