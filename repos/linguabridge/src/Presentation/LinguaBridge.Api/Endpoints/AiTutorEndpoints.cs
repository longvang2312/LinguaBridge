using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.AiTutor.Commands;
using LinguaBridge.Modules.AiTutor.Queries;
using LinguaBridge.Modules.Content;
using Microsoft.AspNetCore.Mvc;

namespace LinguaBridge.Api.Endpoints;

/// <param name="TargetLanguage">"en" or "zh".</param>
/// <param name="ScenarioCode">JobInterview, BusinessEmail or FreeTalk.</param>
public sealed record CreateSessionRequest(string? TargetLanguage, string? ScenarioCode, string? Title);

public sealed record SendMessageRequest(string? Text);

/// <summary>
/// The learner's AI tutor. Like the Learning endpoints they act on the signed-in learner only: the user id comes from
/// the token, so another learner's session cannot be named. Two failures are not use-case results: an exhausted
/// allowance is 429 and an unavailable model is 503.
/// </summary>
public static class AiTutorEndpoints
{
    private const string Prefix = "/api/v1/ai";

    public static IEndpointRouteBuilder MapAiTutorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix).WithTags("AI Tutor");

        group.MapPost("/sessions", CreateSession);
        group.MapPost("/sessions/{sessionId:guid}/messages", SendMessage);
        group.MapGet("/sessions/{sessionId:guid}/messages", GetMessages);
        group.MapDelete("/sessions/{sessionId:guid}", EndSession);
        group.MapGet("/quota", GetQuota);

        return app;
    }

    private static async Task<IResult> CreateSession(CreateSessionRequest request, ICurrentUser user, CreateSessionHandler handler, CancellationToken cancellationToken)
    {
        if (LearningEndpoints.RequireLearner(user, out var denied) is not { } userId)
            return denied!;
        if (!ContentParsing.TryParseTargetLanguage(request.TargetLanguage, out var language))
            return Error.Validation("targetLanguage", "'targetLanguage' must be 'en' or 'zh'.").ToProblem();

        var result = await handler.HandleAsync(new CreateSessionCommand(userId, language, request.ScenarioCode ?? string.Empty, request.Title), cancellationToken);
        return result.IsSuccess
            ? Results.Created($"{Prefix}/sessions/{result.Value.Id}/messages", result.Value)
            : result.Error!.ToProblem();
    }

    private static async Task<IResult> SendMessage(
        Guid sessionId,
        SendMessageRequest request,
        [FromQuery] string? baseLanguage,
        HttpContext http,
        ICurrentUser user,
        SendMessageHandler handler,
        CancellationToken cancellationToken)
    {
        if (LearningEndpoints.RequireLearner(user, out var denied) is not { } userId)
            return denied!;
        if (ContentEndpoints.ParseBaseLanguage(baseLanguage) is not { } language)
            return ContentEndpoints.InvalidBaseLanguage();

        try
        {
            return (await handler.HandleAsync(new SendMessageCommand(userId, sessionId, request.Text ?? string.Empty, language), cancellationToken)).ToOk();
        }
        catch (AiQuotaExceededException ex)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling((ex.ResetsAt - DateTimeOffset.UtcNow).TotalSeconds));
            http.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests",
                extensions: new Dictionary<string, object?> { ["code"] = "ai.quota_exhausted", ["resetsAt"] = ex.ResetsAt });
        }
        catch (LlmProviderException)
        {
            // The cause (a key, an address, the provider's reply) is logged by the provider and is not for the learner.
            return Results.Problem(
                detail: "The AI tutor is temporarily unavailable. Please try again shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service unavailable",
                extensions: new Dictionary<string, object?> { ["code"] = "ai.provider_unavailable" });
        }
    }

    private static async Task<IResult> GetMessages(
        Guid sessionId,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        ICurrentUser user,
        GetMessagesHandler handler,
        CancellationToken cancellationToken)
    {
        if (LearningEndpoints.RequireLearner(user, out var denied) is not { } userId)
            return denied!;

        var query = new GetMessagesQuery(userId, sessionId, offset ?? 0, limit ?? GetMessagesQuery.DefaultLimit);
        return (await handler.HandleAsync(query, cancellationToken)).ToOk();
    }

    private static async Task<IResult> EndSession(Guid sessionId, ICurrentUser user, EndSessionHandler handler, CancellationToken cancellationToken)
    {
        if (LearningEndpoints.RequireLearner(user, out var denied) is not { } userId)
            return denied!;

        return (await handler.HandleAsync(new EndSessionCommand(userId, sessionId), cancellationToken)).ToNoContent();
    }

    private static async Task<IResult> GetQuota(ICurrentUser user, GetQuotaHandler handler, CancellationToken cancellationToken)
    {
        if (LearningEndpoints.RequireLearner(user, out var denied) is not { } userId)
            return denied!;

        return Results.Ok(await handler.HandleAsync(new GetQuotaQuery(userId), cancellationToken));
    }
}
