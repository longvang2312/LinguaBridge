using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.Extensions.Logging;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

/// <summary>
/// Persists <see cref="AuditLog"/> records. Old/new values are serialized to JSON and any property whose name contains
/// "password" or "secret" is replaced by a placeholder as a last line of defence against leaking credentials.
/// </summary>
public sealed partial class AuditService : IAuditService
{
    internal const string Redacted = "[REDACTED]";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly AppAuditDbContext _context;
    private readonly ICurrentCaller _caller;
    private readonly ILogger<AuditService> _logger;

    public AuditService(AppAuditDbContext context, ICurrentCaller caller, ILogger<AuditService> logger)
    {
        _context = context;
        _caller = caller;
        _logger = logger;
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var log = new AuditLog(
            entry.Action,
            entry.EntityName,
            entry.EntityId,
            userId: _caller.UserId,
            projectId: entry.ProjectId,
            oldValuesJson: Serialize(entry.OldValues),
            newValuesJson: Serialize(entry.NewValues),
            ipAddress: _caller.IpAddress);

        try
        {
            _context.AuditLogs.Add(log);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The change itself is already committed; surface the problem loudly instead of losing the audit trail silently.
            _logger.LogCritical(ex, "Could not write the audit record {Action} {EntityName} {EntityId}.", entry.Action, entry.EntityName, entry.EntityId);
            throw;
        }
    }

    internal static string? Serialize(object? value)
    {
        if (value is null)
            return null;

        var node = JsonSerializer.SerializeToNode(value, SerializerOptions);
        Redact(node);
        return node?.ToJsonString();
    }

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (SensitiveName().IsMatch(key))
                        obj[key] = Redacted;
                    else
                        Redact(obj[key]);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                    Redact(item);
                break;
        }
    }

    [GeneratedRegex("password|secret", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();
}
