using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

public sealed class EfAdminAccessReader : IAdminAccessReader
{
    private readonly IPermissionDataSource _dataSource;
    private readonly AppAuthDbContext _auth;

    public EfAdminAccessReader(IPermissionDataSource dataSource, AppAuthDbContext auth)
    {
        _dataSource = dataSource;
        _auth = auth;
    }

    public async Task<AdminAccess> ReadAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await _dataSource.GetUserAccessStateAsync(userId, cancellationToken);
        if (user is null || !user.IsActive || user.LockoutEnd > DateTimeOffset.UtcNow)
            return AdminAccess.None;

        var isGlobalAdmin = await _dataSource.IsGlobalAdminAsync(userId, cancellationToken);

        // Only projects that are alive and in which the caller is an unblocked administrator count.
        var projectIds = await _auth.ProjectUserAssignments.AsNoTracking()
            .Where(a => a.UserId == userId && a.IsProjectAdmin && !a.IsBlocked
                        && a.Project.IsActive && !a.Project.IsDeleted)
            .Select(a => a.ProjectId)
            .ToListAsync(cancellationToken);

        return new AdminAccess(true, isGlobalAdmin, projectIds.ToHashSet());
    }
}
