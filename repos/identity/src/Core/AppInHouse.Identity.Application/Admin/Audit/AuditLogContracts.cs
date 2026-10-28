using System.ComponentModel.DataAnnotations;
using AppInHouse.Identity.Application.Admin.Common;

namespace AppInHouse.Identity.Application.Admin.Audit;

public sealed record AuditLogDto(
    Guid Id,
    string? UserId,
    Guid? ProjectId,
    string Action,
    string EntityName,
    string EntityId,
    string? OldValuesJson,
    string? NewValuesJson,
    string? IpAddress,
    DateTime Timestamp);

public class AuditLogQuery : PagingQuery
{
    /// <summary>The user who performed the action.</summary>
    [StringLength(450)]
    public string? ActorId { get; set; }

    public Guid? ProjectId { get; set; }

    [StringLength(100)]
    public string? EntityName { get; set; }

    [StringLength(50)]
    public string? Action { get; set; }

    /// <summary>Inclusive lower bound (UTC).</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>Inclusive upper bound (UTC).</summary>
    public DateTimeOffset? To { get; set; }
}

public interface IAuditLogQueryService
{
    /// <summary>
    /// Global administrators see everything; project administrators only the entries of the projects they administer.
    /// </summary>
    Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default);
}
