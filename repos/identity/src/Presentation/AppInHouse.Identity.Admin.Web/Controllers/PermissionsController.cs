using AppInHouse.Identity.Admin.Web.Infrastructure;
using AppInHouse.Identity.Admin.Web.ViewModels;
using AppInHouse.Identity.Application.Admin.Permissions;
using AppInHouse.Identity.Application.Admin.Projects;
using AppInHouse.Identity.Application.Admin.Security;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.Identity.Admin.Web.Controllers;

public class PermissionsController : PortalController
{
    private readonly IPermissionAdminService _permissions;
    private readonly IProjectAdminService _projects;
    private readonly IAdminAuthorizer _authorizer;

    public PermissionsController(IPermissionAdminService permissions, IProjectAdminService projects, IAdminAuthorizer authorizer)
    {
        _permissions = permissions;
        _projects = projects;
        _authorizer = authorizer;
    }

    public async Task<IActionResult> Index(PermissionListQuery query, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            query = new PermissionListQuery();

        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        var result = await _permissions.ListAsync(query, cancellationToken);
        var projects = await ProjectsAsync(cancellationToken);
        return View(new PermissionListViewModel(query, result.Items, PagerModel.From(result), projects, caller.IsGlobalAdmin));
    }

    [HttpGet]
    public async Task<IActionResult> Create(Guid? projectId, CancellationToken cancellationToken)
        => View(await NewFormAsync(new CreatePermissionRequest { ProjectId = projectId }, null, true, cancellationToken));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind(Prefix = "Form")] CreatePermissionRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return View(await NewFormAsync(request, null, true, cancellationToken));

        var permission = await _permissions.CreateAsync(request, cancellationToken);
        Success($"Permission {permission.PermissionCode} was created.");
        return RedirectToAction(nameof(Index), new { module = permission.Module });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var permission = await _permissions.GetAsync(id, cancellationToken);
        var form = new CreatePermissionRequest
        {
            PermissionCode = permission.PermissionCode,
            DisplayName = permission.DisplayName,
            Module = permission.Module,
            ProjectId = permission.ProjectId,
        };
        return View(await NewFormAsync(form, id, permission.IsActive, cancellationToken));
    }

    /// <summary>The code and the project of a permission are immutable, so only the descriptive fields and the status are read.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, UpdatePermissionRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var permission = await _permissions.GetAsync(id, cancellationToken);
            var form = new CreatePermissionRequest
            {
                PermissionCode = permission.PermissionCode,
                DisplayName = request.DisplayName,
                Module = request.Module,
                ProjectId = permission.ProjectId,
            };
            return View(await NewFormAsync(form, id, request.IsActive ?? permission.IsActive, cancellationToken));
        }

        var updated = await _permissions.UpdateAsync(id, request, cancellationToken);
        Success($"Permission {updated.PermissionCode} was updated.");
        return RedirectToAction(nameof(Index), new { module = updated.Module });
    }

    private async Task<PermissionFormViewModel> NewFormAsync(CreatePermissionRequest form, Guid? id, bool isActive, CancellationToken cancellationToken)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        return new PermissionFormViewModel
        {
            Id = id,
            Form = form,
            IsActive = isActive,
            Projects = await ProjectsAsync(cancellationToken),
            IsGlobalAdmin = caller.IsGlobalAdmin,
        };
    }

    private Task<List<ProjectDto>> ProjectsAsync(CancellationToken cancellationToken)
        => AllAsync(page => _projects.ListAsync(new ProjectListQuery { Page = page, PageSize = 100 }, cancellationToken));
}
