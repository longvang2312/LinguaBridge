using AppInHouse.Identity.Admin.Web.Infrastructure;
using AppInHouse.Identity.Admin.Web.ViewModels;
using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Projects;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.Identity.Admin.Web.Controllers;

public class AuditLogsController : PortalController
{
    private static readonly string[] Entities =
    {
        AuditEntities.Project, AuditEntities.ProjectUser, AuditEntities.UserRole, AuditEntities.UserPermission,
        AuditEntities.Client, AuditEntities.User, AuditEntities.Permission, AuditEntities.Role,
    };

    private static readonly string[] Actions =
    {
        AuditActions.Create, AuditActions.Update, AuditActions.StatusChange, AuditActions.Assign, AuditActions.SetRoles,
        AuditActions.SetPermissions, AuditActions.Lock, AuditActions.Unlock, AuditActions.ResetPassword, AuditActions.RotateSecret,
    };

    private readonly IAuditLogQueryService _audit;
    private readonly IProjectAdminService _projects;

    public AuditLogsController(IAuditLogQueryService audit, IProjectAdminService projects)
    {
        _audit = audit;
        _projects = projects;
    }

    public async Task<IActionResult> Index(AuditFilter filter, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Failure("The filter was not understood and was reset.");
            filter = new AuditFilter();
        }

        var result = await _audit.QueryAsync(filter.ToQuery(), cancellationToken);
        var projects = await AllAsync(page => _projects.ListAsync(new ProjectListQuery { Page = page, PageSize = 100 }, cancellationToken));
        return View(new AuditListViewModel(filter, result.Items, PagerModel.From(result), projects, Entities, Actions));
    }
}
