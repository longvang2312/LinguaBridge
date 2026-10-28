using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Common;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace AppInHouse.Identity.Admin.Api.Controllers;

[Route($"{Root}/audit-logs")]
[SwaggerTag("Audit trail of administrative operations")]
public class AuditLogsController : AdminControllerBase
{
    private readonly IAuditLogQueryService _auditLogs;

    public AuditLogsController(IAuditLogQueryService auditLogs) => _auditLogs = auditLogs;

    [HttpGet]
    [SwaggerOperation(
        Summary = "Searches the audit trail",
        Description = "Filter by actor, project, entity, action and time range. Global administrators see everything, project administrators only their projects.")]
    [ProducesResponseType(typeof(PagedResult<AuditLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Query([FromQuery] AuditLogQuery query, CancellationToken cancellationToken)
        => Ok(await _auditLogs.QueryAsync(query, cancellationToken));
}
