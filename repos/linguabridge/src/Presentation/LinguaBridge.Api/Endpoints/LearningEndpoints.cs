using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content;
using LinguaBridge.Modules.Learning.Commands;
using LinguaBridge.Modules.Learning.Queries;
using Microsoft.AspNetCore.Mvc;

namespace LinguaBridge.Api.Endpoints;

public sealed record StartAttemptRequest(Guid LessonId);

public sealed record SubmitResponseRequest(Guid QuestionId, Guid? SelectedOptionId, string? UserInputText, int TimeSpentMs);

public sealed record ReviewCardRequest(Guid CardId, int Rating);

/// <summary>
/// The learner's own endpoints. They act on the signed-in learner only: the user id always comes from the token,
/// never from the request, so there is no way to name someone else's attempt or card.
/// </summary>
public static class LearningEndpoints
{
    private const string Prefix = "/api/v1/learning";

    public static IEndpointRouteBuilder MapLearningEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix).WithTags("Learning");

        group.MapGet("/lessons/{lessonId:guid}/quiz", GetQuiz);
        group.MapPost("/attempts", StartAttempt);
        group.MapPost("/attempts/{attemptId:guid}/responses", SubmitResponse);
        group.MapPut("/attempts/{attemptId:guid}/complete", CompleteAttempt);
        group.MapGet("/srs/due", GetDueCards);
        group.MapPost("/srs/review", ReviewCard);
        group.MapGet("/progress/{courseId:guid}", GetProgress);

        return app;
    }

    private static async Task<IResult> GetQuiz(
        Guid lessonId,
        [FromQuery] string? baseLanguage,
        ICurrentUser user,
        GetQuizHandler handler,
        CancellationToken cancellationToken)
    {
        if (RequireLearner(user, out var denied) is null)
            return denied!;
        if (ContentEndpoints.ParseBaseLanguage(baseLanguage) is not { } language)
            return ContentEndpoints.InvalidBaseLanguage();

        return (await handler.HandleAsync(new GetQuizQuery(lessonId, language), cancellationToken)).ToOk();
    }

    private static async Task<IResult> StartAttempt(StartAttemptRequest request, ICurrentUser user, StartAttemptHandler handler, CancellationToken cancellationToken)
    {
        if (RequireLearner(user, out var denied) is not { } userId)
            return denied!;

        var result = await handler.HandleAsync(new StartAttemptCommand(userId, request.LessonId), cancellationToken);
        return result.IsSuccess
            ? Results.Created($"{Prefix}/attempts/{result.Value.AttemptId}", result.Value)
            : result.Error!.ToProblem();
    }

    private static async Task<IResult> SubmitResponse(
        Guid attemptId,
        SubmitResponseRequest request,
        [FromQuery] string? baseLanguage,
        ICurrentUser user,
        SubmitResponseHandler handler,
        CancellationToken cancellationToken)
    {
        if (RequireLearner(user, out var denied) is not { } userId)
            return denied!;
        if (ContentEndpoints.ParseBaseLanguage(baseLanguage) is not { } language)
            return ContentEndpoints.InvalidBaseLanguage();

        var command = new SubmitResponseCommand(userId, attemptId, request.QuestionId, request.SelectedOptionId, request.UserInputText, request.TimeSpentMs, language);
        return (await handler.HandleAsync(command, cancellationToken)).ToOk();
    }

    private static async Task<IResult> CompleteAttempt(Guid attemptId, ICurrentUser user, CompleteAttemptHandler handler, CancellationToken cancellationToken)
    {
        if (RequireLearner(user, out var denied) is not { } userId)
            return denied!;

        return (await handler.HandleAsync(new CompleteAttemptCommand(userId, attemptId), cancellationToken)).ToOk();
    }

    private static async Task<IResult> GetDueCards(
        [FromQuery] string? baseLanguage,
        [FromQuery] string? targetLanguage,
        [FromQuery] int? limit,
        ICurrentUser user,
        GetDueCardsHandler handler,
        CancellationToken cancellationToken)
    {
        if (RequireLearner(user, out var denied) is not { } userId)
            return denied!;
        if (ContentEndpoints.ParseBaseLanguage(baseLanguage) is not { } language)
            return ContentEndpoints.InvalidBaseLanguage();

        TargetLanguage? target = null;
        if (!string.IsNullOrWhiteSpace(targetLanguage))
        {
            if (!ContentParsing.TryParseTargetLanguage(targetLanguage, out var parsed))
                return Error.Validation("targetLanguage", "'targetLanguage' must be 'en' or 'zh'.").ToProblem();
            target = parsed;
        }

        if (limit is < 1 or > GetDueCardsQuery.MaxLimit)
            return Error.Validation("limit", $"'limit' must be between 1 and {GetDueCardsQuery.MaxLimit}.").ToProblem();

        var query = new GetDueCardsQuery(userId, language, target, limit ?? GetDueCardsQuery.DefaultLimit);
        return (await handler.HandleAsync(query, cancellationToken)).ToOk();
    }

    private static async Task<IResult> ReviewCard(ReviewCardRequest request, ICurrentUser user, ReviewCardHandler handler, CancellationToken cancellationToken)
    {
        if (RequireLearner(user, out var denied) is not { } userId)
            return denied!;

        return (await handler.HandleAsync(new ReviewCardCommand(userId, request.CardId, request.Rating), cancellationToken)).ToOk();
    }

    private static async Task<IResult> GetProgress(Guid courseId, ICurrentUser user, GetCourseProgressHandler handler, CancellationToken cancellationToken)
    {
        if (RequireLearner(user, out var denied) is not { } userId)
            return denied!;

        return (await handler.HandleAsync(new GetCourseProgressQuery(userId, courseId), cancellationToken)).ToOk();
    }

    /// <summary>
    /// A token that is valid but names no learner (for example a service's client_credentials token, whose subject is
    /// not a user id) cannot own attempts or cards, so it is refused rather than given an empty account.
    /// </summary>
    internal static Guid? RequireLearner(ICurrentUser user, out IResult? denied)
    {
        if (user.UserId is { } userId)
        {
            denied = null;
            return userId;
        }

        denied = Results.Problem(
            detail: "This endpoint needs a token issued to a learner (a user), not to a service.",
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            extensions: new Dictionary<string, object?> { ["code"] = "learner.required" });
        return null;
    }
}
