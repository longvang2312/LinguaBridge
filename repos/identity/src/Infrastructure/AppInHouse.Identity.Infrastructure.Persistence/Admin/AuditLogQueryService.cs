using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

public sealed class AuditLogQueryService : IAuditLogQueryService
{
    private readonly AppAuditDbContext _context;
    private readonly IAdminAuthorizer _authorizer;

    public AuditLogQueryService(AppAuditDbContext context, IAdminAuthorizer authorizer)
    {
        _context = context;
        _authorizer = authorizer;
    }

    public async Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireAnyAdmin();

        var logs = _context.AuditLogs.AsNoTracking();

        if (query.ProjectId is { } projectId)
        {
            caller.RequireProjectAdmin(projectId);
            logs = logs.Where(l => l.ProjectId == projectId);
        }
        else if (!caller.IsGlobalAdmin)
        {
            // Project administrators only see the trail of the projects they administer.
            var visible = caller.AdministeredProjectIds.ToList();
            logs = logs.Where(l => l.ProjectId != null && visible.Contains(l.ProjectId.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.ActorId))
        {
            var actor = query.ActorId.Trim();
            logs = logs.Where(l => l.UserId == actor);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityName))
        {
            var entity = query.EntityName.Trim();
            logs = logs.Where(l => l.EntityName == entity);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.Trim();
            logs = logs.Where(l => l.Action == action);
        }

        if (query.From is { } from)
        {
            var fromUtc = from.UtcDateTime;
            logs = logs.Where(l => l.Timestamp >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = to.UtcDateTime;
            logs = logs.Where(l => l.Timestamp <= toUtc);
        }

        if (query.From is { } f && query.To is { } t && f > t)
            throw new BadRequestException("From must not be later than To.");

        return await logs
            .OrderByDescending(l => l.Timestamp)
            .ThenByDescending(l => l.Id)
            .Select(l => new AuditLogDto(
                l.Id, l.UserId, l.ProjectId, l.Action, l.EntityName, l.EntityId,
                l.OldValuesJson, l.NewValuesJson, l.IpAddress, l.Timestamp))
            .ToPagedResultAsync(query, cancellationToken);
    }
}
