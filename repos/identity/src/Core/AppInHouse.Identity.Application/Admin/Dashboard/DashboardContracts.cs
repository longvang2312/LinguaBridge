using AppInHouse.Identity.Application.Admin.Audit;

namespace AppInHouse.Identity.Application.Admin.Dashboard;

/// <summary>
/// Platform totals for the administration portal. For a project administrator every figure is limited to what they may
/// see (their projects and the global permissions); <see cref="Clients"/> and <see cref="Users"/> are global-only (null otherwise).
/// </summary>
public sealed record DashboardDto(
    int Projects,
    int ActiveProjects,
    int? Clients,
    int? Users,
    int Roles,
    int Permissions,
    IReadOnlyList<AuditLogDto> RecentAudit);

public interface IDashboardService
{
    Task<DashboardDto> GetAsync(CancellationToken cancellationToken = default);
}
