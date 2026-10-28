using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Dashboard;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

public sealed class DashboardService : IDashboardService
{
    private const int RecentAuditCount = 8;

    private readonly AppAuthDbContext _auth;
    private readonly AppIdentityDbContext _identity;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IAuditLogQueryService _audit;
    private readonly IAdminAuthorizer _authorizer;

    public DashboardService(
        AppAuthDbContext auth,
        AppIdentityDbContext identity,
        IOpenIddictApplicationManager applications,
        IAuditLogQueryService audit,
        IAdminAuthorizer authorizer)
    {
        _auth = auth;
        _identity = identity;
        _applications = applications;
        _audit = audit;
        _authorizer = authorizer;
    }

    public async Task<DashboardDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireAnyAdmin();

        var projects = _auth.Projects.AsNoTracking().Where(p => !p.IsDeleted);
        var permissions = _auth.Permissions.AsNoTracking();
        if (!caller.IsGlobalAdmin)
        {
            var visible = caller.AdministeredProjectIds.ToList();
            projects = projects.Where(p => visible.Contains(p.Id));
            permissions = permissions.Where(p => p.IsGlobal || (p.ProjectId != null && visible.Contains(p.ProjectId.Value)));
        }

        var projectCount = await projects.CountAsync(cancellationToken);
        var activeProjectCount = await projects.CountAsync(p => p.IsActive, cancellationToken);
        var permissionCount = await permissions.CountAsync(cancellationToken);
        var roleCount = await _identity.Roles.AsNoTracking().CountAsync(cancellationToken);

        int? clientCount = null;
        int? userCount = null;
        if (caller.IsGlobalAdmin)
        {
            clientCount = (int)await _applications.CountAsync(cancellationToken);
            userCount = await _identity.Users.AsNoTracking().CountAsync(cancellationToken);
        }

        var recent = await _audit.QueryAsync(new AuditLogQuery { Page = 1, PageSize = RecentAuditCount }, cancellationToken);

        return new DashboardDto(projectCount, activeProjectCount, clientCount, userCount, roleCount, permissionCount, recent.Items);
    }
}
