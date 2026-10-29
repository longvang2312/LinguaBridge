using LinguaBridge.Api.Auth;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Billing.Abstractions;
using LinguaBridge.Modules.Billing.Commands;
using LinguaBridge.Modules.Billing.Queries;

namespace LinguaBridge.Api.Endpoints;

public sealed record CreatePlanRequest(string? Id, string? Name, decimal? PriceVnd, int? DurationDays, int? AiDailyTokenQuota);

public static class BillingEndpoints
{
    private const string Prefix = "/api/v1/billing";

    /// <summary>A payment notification is a few kilobytes; anything near this is not one, and is not read.</summary>
    private const int MaxWebhookBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix).WithTags("Billing");

        group.MapGet("/plans", GetPlans).AllowAnonymous();
        group.MapPost("/plans", CreatePlan).RequireAuthorization(Policies.Admin);
        group.MapGet("/entitlement", GetEntitlement);

        // Gateways cannot sign in; the signature on each message is what authenticates it.
        group.MapPost("/webhooks/{provider}", ReceiveWebhook).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> GetPlans(GetPlansHandler handler, CancellationToken cancellationToken) =>
        Results.Ok(await handler.HandleAsync(cancellationToken));

    private static async Task<IResult> CreatePlan(CreatePlanRequest request, CreatePlanHandler handler, CancellationToken cancellationToken)
    {
        if (request.PriceVnd is null || request.DurationDays is null || request.AiDailyTokenQuota is null)
        {
            var missing = new Dictionary<string, string[]>();
            if (request.PriceVnd is null) missing["PriceVnd"] = ["The price is required."];
            if (request.DurationDays is null) missing["DurationDays"] = ["The duration is required."];
            if (request.AiDailyTokenQuota is null) missing["AiDailyTokenQuota"] = ["The AI daily token quota is required."];
            return Error.Validation(missing).ToProblem();
        }

        var command = new CreatePlanCommand(request.Id ?? string.Empty, request.Name ?? string.Empty, request.PriceVnd.Value, request.DurationDays.Value, request.AiDailyTokenQuota.Value);
        var result = await handler.HandleAsync(command, cancellationToken);
        return result.IsSuccess ? Results.Created($"{Prefix}/plans", result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> GetEntitlement(ICurrentUser user, GetEntitlementHandler handler, CancellationToken cancellationToken)
    {
        if (LearningEndpoints.RequireLearner(user, out var denied) is not { } userId)
            return denied!;

        return Results.Ok(await handler.HandleAsync(new GetEntitlementQuery(userId), cancellationToken));
    }

    /// <summary>
    /// 200 for everything the gateway should stop resending (processed, already processed, not relevant), 400 for a
    /// message that is not genuine or not readable (never stored), 422 for a genuine payment that could not be applied
    /// (stored; the gateway may redeliver it), 503 when this provider has no secret configured.
    /// </summary>
    private static async Task<IResult> ReceiveWebhook(string provider, HttpRequest request, ProcessWebhookHandler handler, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxWebhookBytes)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Payload too large", extensions: Code("webhook.too_large"));

        var body = await ReadBodyAsync(request, cancellationToken);
        if (body is null)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Payload too large", extensions: Code("webhook.too_large"));

        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var result = await handler.HandleAsync(new ProcessWebhookCommand(provider, body, headers), cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Code == "webhook.not_configured"
                ? Results.Problem(detail: result.Error.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable", extensions: Code(result.Error.Code))
                : result.Error.ToProblem();
        }

        return result.Value.Status == WebhookStatuses.Failed
            ? Results.Problem(detail: result.Value.Reason, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Payment could not be applied", extensions: Code("webhook.payment_rejected"))
            : Results.Ok(result.Value);
    }

    /// <summary>The body as sent, or null if it is bigger than allowed. Not parsed here: signatures cover the exact text.</summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var buffer = new char[MaxWebhookBytes + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        return read > MaxWebhookBytes ? null : new string(buffer, 0, read);
    }

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };
}
