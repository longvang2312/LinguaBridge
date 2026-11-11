using System.Security.Claims;
using System.Text.Json;
using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.SharedServices.Api.Endpoints;

/// <summary>
/// Send one email. Give a <see cref="Template"/> (<c>EmailVerification</c>, <c>PasswordReset</c>, <c>SystemNotification</c>) with its
/// <see cref="Variables"/>, or your own <see cref="Subject"/> and body. The project defaults to the calling key's project.
/// </summary>
public sealed record SendEmailRequest(
    string To, string? Template = null, Dictionary<string, string>? Variables = null,
    string? Subject = null, string? HtmlBody = null, string? TextBody = null, string? ProjectCode = null);

/// <summary>
/// Test an email configuration. With <see cref="ConfigurationId"/> the saved version is tested and its last test result is updated.
/// Without it, <see cref="ProviderType"/>, <see cref="Settings"/> and <see cref="Secret"/> are tested from memory and nothing is stored.
/// </summary>
public sealed record SendTestEmailRequest(
    string TestRecipient, Guid? ConfigurationId = null, ProviderType? ProviderType = null, JsonElement? Settings = null, string? Secret = null);

public sealed record SendEmailResponse(bool Success, string Status, Guid? DeliveryLogId, int LatencyMs, string? ConfigurationProject, int? ConfigurationVersion);

public sealed record SendTestEmailResponse(bool Success, string Message, int LatencyMs, string ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

public static class EmailEndpoints
{
    private const string PlatformProject = ProjectAccess.PlatformProject;

    public static IEndpointRouteBuilder MapEmailEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/emails").WithTags("Email");

        group.MapPost("/send", async (SendEmailRequest request, ClaimsPrincipal user, IEmailDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!ProjectAccess.TryResolve(user, request.ProjectCode, out var project)) return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden, title: "Not your project", detail: "An API key can only send for its own project.");

                var result = await dispatcher.SendAsync(new SendEmailCommand(
                    project ?? PlatformProject, request.To, request.Template, request.Variables, request.Subject, request.HtmlBody, request.TextBody), ct);

                return result.Failure switch
                {
                    EmailFailure.None => Results.Ok(new SendEmailResponse(true, "Sent", result.DeliveryLogId, result.LatencyMs, result.ConfigurationProject, result.ConfigurationVersion)),
                    EmailFailure.NoConfiguration => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "No email configuration", detail: result.Error),
                    _ => Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The email could not be delivered", detail: result.Error),
                };
            })
            .RequireAuthorization(Policies.EmailSend)
            .WithName("SendEmail")
            .WithSummary("Sends an email with the project's active email configuration (or the default project's) and logs the delivery.");

        group.MapPost("/test", async (SendTestEmailRequest request, IEmailTestService tests, CancellationToken ct) =>
            {
                var result = await tests.TestAsync(new TestEmailCommand(
                    request.TestRecipient, request.ConfigurationId, request.ProviderType,
                    request.Settings is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } settings ? settings.GetRawText() : null,
                    request.Secret), ct);
                return Results.Ok(new SendTestEmailResponse(result.Success, result.Message, result.LatencyMs, result.ProviderType.ToString(), result.UsedSavedConfiguration, result.ConfigurationId));
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("SendTestEmail")
            .WithSummary("Sends a test email. Without a configuration id the settings in the request are tried from memory and nothing is saved or activated.");

        group.MapGet("/delivery-logs", async ([FromQuery] string? projectCode, [FromQuery] int? limit, ClaimsPrincipal user, IEmailDispatcher dispatcher, CancellationToken ct) =>
            {
                // A key of another project sees only its own deliveries; the platform's keys can look at any project.
                if (!ProjectAccess.TryResolve(user, projectCode, out var project, allowAll: true)) return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden, title: "Not your project", detail: "An API key can only read the deliveries of its own project.");

                return Results.Ok(await dispatcher.RecentAsync(project, limit ?? 20, ct));
            })
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListEmailDeliveryLogs")
            .WithSummary("The most recent email deliveries (no subject, no body).");

        return routes;
    }

}
