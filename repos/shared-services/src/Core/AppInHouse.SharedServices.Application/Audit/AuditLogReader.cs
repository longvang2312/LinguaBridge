using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Audit;

/// <summary>One audit entry as a client may see it. <see cref="DetailsJson"/> has been cleaned of anything that looks like a secret.</summary>
public sealed record AuditLogDto(Guid Id, string Actor, string Action, string ResourceType, string ResourceId, string? DetailsJson, DateTimeOffset CreatedAt);

/// <summary>Filters for the audit list. Null means "any"; <see cref="Actor"/> matches part of the name, <see cref="Action"/> the whole action.</summary>
public sealed record AuditLogQuery(string? Actor = null, string? Action = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 50, int Offset = 0);

public interface IAuditLogRepository
{
    /// <summary>Newest first. <paramref name="to"/> is exclusive.</summary>
    Task<IReadOnlyList<AuditLog>> ListAsync(string? actor, string? action, DateTimeOffset? from, DateTimeOffset? to, int limit, int offset, CancellationToken cancellationToken = default);

    /// <summary>The distinct actions seen so far, to fill a filter.</summary>
    Task<IReadOnlyList<string>> ActionsAsync(CancellationToken cancellationToken = default);
}

public interface IAuditLogReader
{
    Task<IReadOnlyList<AuditLogDto>> ListAsync(AuditLogQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ActionsAsync(CancellationToken cancellationToken = default);
}

public sealed class AuditLogReader(IAuditLogRepository repository) : IAuditLogReader
{
    public const int MaxLimit = 200;

    public async Task<IReadOnlyList<AuditLogDto>> ListAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        if (query.From is { } from && query.To is { } to && to <= from)
            throw new ValidationException(new Dictionary<string, string[]> { ["To"] = ["The end of the period must be after its start."] });

        var actor = string.IsNullOrWhiteSpace(query.Actor) ? null : query.Actor.Trim();
        var action = string.IsNullOrWhiteSpace(query.Action) ? null : query.Action.Trim();
        var rows = await repository.ListAsync(actor, action, query.From, query.To, Math.Clamp(query.Limit, 1, MaxLimit), Math.Max(query.Offset, 0), cancellationToken);
        return [.. rows.Select(r => new AuditLogDto(r.Id, r.Actor, r.Action, r.ResourceType, r.ResourceId, AuditDetails.Sanitize(r.DetailsJson), r.CreatedAt))];
    }

    public Task<IReadOnlyList<string>> ActionsAsync(CancellationToken cancellationToken = default) => repository.ActionsAsync(cancellationToken);
}

/// <summary>
/// Audit details are written without secrets, but they are shown to people, so they are cleaned once more on the way out: the value of
/// any property whose name says "secret" is hidden (a yes/no flag such as <c>SecretChanged</c> is kept), and any text that looks like a
/// key, token or webhook address is hidden wherever it appears.
/// </summary>
public static partial class AuditDetails
{
    public const string Hidden = "••••••••";

    [GeneratedRegex("password|passwd|pwd|secret|token|api[_-]?key|private[_-]?key|credential|authorization|hash", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    // A full API key (the prefix on its own, "aih_live_" and 6 characters, is not a secret), a bot token, a SendGrid key, a webhook
    // address of Discord or Slack, a PEM key.
    [GeneratedRegex(@"aih_live_[A-Za-z0-9]{6}[A-Za-z0-9_-]{16,}|\d{6,12}:[A-Za-z0-9_-]{30,}|SG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}|https://(?:discord(?:app)?\.com/api/webhooks/\d+/[\w-]+|hooks\.slack\.com/services/[A-Z0-9]+/[A-Z0-9]+/\w+)|-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex SecretLooking();

    /// <summary>The cleaned JSON, or null when there was none. Text that is not JSON is cleaned as plain text.</summary>
    public static string? Sanitize(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson)) return null;

        try
        {
            var node = JsonNode.Parse(detailsJson);
            return Clean(node)?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException)
        {
            return SecretLooking().Replace(detailsJson, Hidden);
        }
    }

    private static JsonNode? Clean(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var cleaned = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    var keepAsFlag = value is JsonValue flag && flag.TryGetValue<bool>(out _);
                    cleaned[name] = SensitiveName().IsMatch(name) && !keepAsFlag && value is not null ? JsonValue.Create(Hidden) : Clean(value);
                }

                return cleaned;
            case JsonArray array:
                return new JsonArray([.. array.Select(Clean)]);
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(SecretLooking().Replace(text, Hidden));
            default:
                return node?.DeepClone();
        }
    }
}
