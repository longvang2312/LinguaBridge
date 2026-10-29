using LinguaBridge.Api.Auth;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content;
using LinguaBridge.Modules.Content.Commands;
using LinguaBridge.Modules.Content.Queries;
using Microsoft.AspNetCore.Mvc;

namespace LinguaBridge.Api.Endpoints;

public sealed record CreateCourseRequest(string Code, string TargetLanguage, string Level, string? ThumbnailUrl);

public sealed record UpdateCourseRequest(string Level, string? ThumbnailUrl);

public sealed record AddCourseTranslationRequest(string BaseLanguage, string Title, string? Description);

public sealed record UpdateCourseTranslationRequest(string Title, string? Description);

public sealed record CreateLessonRequest(string LessonType, int? OrderIndex, int? XpReward);

public sealed record AddLessonTranslationRequest(string BaseLanguage, string Title, string? GuideText);

public sealed record AddQuestionRequest(
    string QuestionType,
    string TargetText,
    int? OrderIndex,
    string? PinyinOrIpa,
    string? AudioPromptUrl,
    string? GrammarNotes,
    IReadOnlyList<QuestionTranslationInput>? Translations);

public sealed record AddQuestionOptionRequest(string OptionText, bool IsCorrect, string? PinyinOrIpa);

public static class ContentEndpoints
{
    private const string Prefix = "/api/content";

    /// <summary>The language labels are shown in when none is requested; Vietnamese is the platform's primary audience.</summary>
    private const string DefaultBaseLanguage = "vi";

    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        // Any signed-in caller: what they see depends on their role, decided here and never taken from the request.
        var shared = app.MapGroup(Prefix).WithTags("Content");

        shared.MapGet("/courses", GetPublishedCourses);
        shared.MapGet("/courses/{courseId:guid}", GetCourse);
        shared.MapGet("/courses/{courseId:guid}/lessons", GetLessons);

        var admin = app.MapGroup(Prefix).WithTags("Content (admin)").RequireAuthorization(Policies.Admin);

        admin.MapGet("/courses/admin", GetAllCourses);
        admin.MapPost("/courses", CreateCourse);
        admin.MapPut("/courses/{courseId:guid}", UpdateCourse);
        admin.MapPut("/courses/{courseId:guid}/publish", PublishCourse);
        admin.MapPut("/courses/{courseId:guid}/archive", ArchiveCourse);
        admin.MapPost("/courses/{courseId:guid}/translations", AddCourseTranslation);
        admin.MapPut("/courses/{courseId:guid}/translations/{language}", UpdateCourseTranslation);
        admin.MapPost("/courses/{courseId:guid}/lessons", CreateLesson);
        admin.MapPost("/lessons/{lessonId:guid}/publish", PublishLesson);
        admin.MapPost("/lessons/{lessonId:guid}/translations", AddLessonTranslation);
        admin.MapGet("/lessons/{lessonId:guid}/preview", PreviewLesson);
        admin.MapPost("/lessons/{lessonId:guid}/questions", AddQuestion);
        admin.MapPost("/questions/{questionId:guid}/options", AddQuestionOption);

        return app;
    }

    // ---- Reads --------------------------------------------------------------------------------------------

    private static async Task<IResult> GetPublishedCourses(
        [FromQuery] string? baseLanguage,
        [FromQuery] string? targetLanguage,
        GetPublishedCoursesHandler handler,
        CancellationToken cancellationToken)
    {
        if (ParseBaseLanguage(baseLanguage) is not { } language)
            return InvalidBaseLanguage();
        if (!ContentParsing.TryParseTargetLanguage(targetLanguage, out var target))
            return Error.Validation("targetLanguage", "'targetLanguage' is required and must be 'en' or 'zh'.").ToProblem();

        return (await handler.HandleAsync(new GetPublishedCoursesQuery(language, target), cancellationToken)).ToOk();
    }

    private static async Task<IResult> GetAllCourses(
        [FromQuery] string? baseLanguage,
        [FromQuery] string? targetLanguage,
        GetAllCoursesHandler handler,
        CancellationToken cancellationToken)
    {
        if (ParseBaseLanguage(baseLanguage) is not { } language)
            return InvalidBaseLanguage();

        TargetLanguage? target = null;
        if (!string.IsNullOrWhiteSpace(targetLanguage))
        {
            if (!ContentParsing.TryParseTargetLanguage(targetLanguage, out var parsed))
                return Error.Validation("targetLanguage", "'targetLanguage' must be 'en' or 'zh'.").ToProblem();
            target = parsed;
        }

        return (await handler.HandleAsync(new GetAllCoursesQuery(target, language), cancellationToken)).ToOk();
    }

    private static async Task<IResult> GetCourse(
        Guid courseId,
        [FromQuery] string? baseLanguage,
        ICurrentUser user,
        GetCourseDetailHandler handler,
        CancellationToken cancellationToken)
    {
        if (ParseBaseLanguage(baseLanguage) is not { } language)
            return InvalidBaseLanguage();

        var query = new GetCourseDetailQuery(courseId, language, IncludeUnpublished: user.IsInRole(Roles.GlobalAdmin));
        return (await handler.HandleAsync(query, cancellationToken)).ToOk();
    }

    private static async Task<IResult> GetLessons(
        Guid courseId,
        [FromQuery] string? baseLanguage,
        ICurrentUser user,
        GetLessonsHandler handler,
        CancellationToken cancellationToken)
    {
        if (ParseBaseLanguage(baseLanguage) is not { } language)
            return InvalidBaseLanguage();

        var query = new GetLessonsQuery(courseId, language, IncludeUnpublished: user.IsInRole(Roles.GlobalAdmin));
        return (await handler.HandleAsync(query, cancellationToken)).ToOk();
    }

    private static async Task<IResult> PreviewLesson(
        Guid lessonId,
        [FromQuery] string? baseLanguage,
        PreviewLessonHandler handler,
        CancellationToken cancellationToken)
    {
        if (ParseBaseLanguage(baseLanguage) is not { } language)
            return InvalidBaseLanguage();

        return (await handler.HandleAsync(new PreviewLessonQuery(lessonId, language), cancellationToken)).ToOk();
    }

    // ---- Courses ------------------------------------------------------------------------------------------

    private static async Task<IResult> CreateCourse(CreateCourseRequest request, CreateCourseHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new CreateCourseCommand(request.Code, request.TargetLanguage, request.Level, request.ThumbnailUrl),
            cancellationToken);
        return result.ToCreated(id => $"{Prefix}/courses/{id}");
    }

    private static async Task<IResult> UpdateCourse(Guid courseId, UpdateCourseRequest request, UpdateCourseHandler handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new UpdateCourseCommand(courseId, request.Level, request.ThumbnailUrl), cancellationToken)).ToNoContent();

    private static async Task<IResult> PublishCourse(Guid courseId, PublishCourseHandler handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PublishCourseCommand(courseId), cancellationToken)).ToNoContent();

    private static async Task<IResult> ArchiveCourse(Guid courseId, ArchiveCourseHandler handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ArchiveCourseCommand(courseId), cancellationToken)).ToNoContent();

    private static async Task<IResult> AddCourseTranslation(Guid courseId, AddCourseTranslationRequest request, AddCourseTranslationHandler handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AddCourseTranslationCommand(courseId, request.BaseLanguage, request.Title, request.Description), cancellationToken)).ToNoContent();

    private static async Task<IResult> UpdateCourseTranslation(Guid courseId, string language, UpdateCourseTranslationRequest request, UpdateCourseTranslationHandler handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new UpdateCourseTranslationCommand(courseId, language, request.Title, request.Description), cancellationToken)).ToNoContent();

    // ---- Lessons ------------------------------------------------------------------------------------------

    private static async Task<IResult> CreateLesson(Guid courseId, CreateLessonRequest request, CreateLessonHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateLessonCommand(courseId, request.LessonType, request.OrderIndex, request.XpReward), cancellationToken);
        return result.ToCreated(id => $"{Prefix}/lessons/{id}/preview");
    }

    private static async Task<IResult> PublishLesson(Guid lessonId, PublishLessonHandler handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PublishLessonCommand(lessonId), cancellationToken)).ToNoContent();

    private static async Task<IResult> AddLessonTranslation(Guid lessonId, AddLessonTranslationRequest request, AddLessonTranslationHandler handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AddLessonTranslationCommand(lessonId, request.BaseLanguage, request.Title, request.GuideText), cancellationToken)).ToNoContent();

    // ---- Questions ----------------------------------------------------------------------------------------

    private static async Task<IResult> AddQuestion(Guid lessonId, AddQuestionRequest request, AddQuestionHandler handler, CancellationToken cancellationToken)
    {
        var command = new AddQuestionCommand(
            lessonId,
            request.QuestionType,
            request.TargetText,
            request.OrderIndex,
            request.PinyinOrIpa,
            request.AudioPromptUrl,
            request.GrammarNotes,
            request.Translations);
        return (await handler.HandleAsync(command, cancellationToken)).ToCreated(_ => $"{Prefix}/lessons/{lessonId}/preview");
    }

    private static async Task<IResult> AddQuestionOption(Guid questionId, AddQuestionOptionRequest request, AddQuestionOptionHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new AddQuestionOptionCommand(questionId, request.OptionText, request.IsCorrect, request.PinyinOrIpa), cancellationToken);
        return result.ToCreated(id => $"{Prefix}/questions/{questionId}/options/{id}");
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>Null when the code is present but not a supported language.</summary>
    internal static BaseLanguage? ParseBaseLanguage(string? code) =>
        ContentParsing.TryParseBaseLanguage(string.IsNullOrWhiteSpace(code) ? DefaultBaseLanguage : code, out var language) ? language : null;

    internal static IResult InvalidBaseLanguage() =>
        Error.Validation("baseLanguage", "'baseLanguage' must be 'vi' or 'en'.").ToProblem();
}
