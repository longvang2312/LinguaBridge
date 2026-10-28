using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Projects;

namespace AppInHouse.Identity.Admin.Web.ViewModels;

/// <summary>The filter of the audit trail as typed in the form (whole days, UTC).</summary>
public sealed class AuditFilter
{
    public string? ActorId { get; set; }

    public Guid? ProjectId { get; set; }

    public string? EntityName { get; set; }

    /// <remarks>Not called "Action": a route value of that name would be bound to it.</remarks>
    public string? Operation { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 20;

    public AuditLogQuery ToQuery() => new()
    {
        ActorId = ActorId,
        ProjectId = ProjectId,
        EntityName = EntityName,
        Action = Operation,
        From = From is { } from ? new DateTimeOffset(DateTime.SpecifyKind(from.Date, DateTimeKind.Utc)) : null,
        // The whole last day is included.
        To = To is { } to ? new DateTimeOffset(DateTime.SpecifyKind(to.Date, DateTimeKind.Utc)).AddDays(1).AddTicks(-1) : null,
        Page = Page,
        PageSize = PageSize,
    };
}

public sealed record AuditListViewModel(
    AuditFilter Filter,
    IReadOnlyList<AuditLogDto> Items,
    PagerModel Pager,
    IReadOnlyList<ProjectDto> Projects,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Actions);
