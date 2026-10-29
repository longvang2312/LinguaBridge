using FluentValidation;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content.Abstractions;
using LinguaBridge.Modules.Content.Entities;

namespace LinguaBridge.Modules.Content.Commands;

/// <param name="TargetLanguage"><c>en</c> or <c>zh</c>.</param>
/// <param name="Level"><c>A1</c> to <c>C2</c> for English, <c>HSK1</c> to <c>HSK6</c> for Chinese.</param>
public sealed record CreateCourseCommand(string Code, string TargetLanguage, string Level, string? ThumbnailUrl = null);

/// <summary>Replaces the editable metadata. <c>ThumbnailUrl</c> of null clears it.</summary>
public sealed record UpdateCourseCommand(Guid CourseId, string Level, string? ThumbnailUrl);

public sealed record PublishCourseCommand(Guid CourseId);

public sealed record ArchiveCourseCommand(Guid CourseId);

public sealed record AddCourseTranslationCommand(Guid CourseId, string BaseLanguage, string Title, string? Description = null);

public sealed record UpdateCourseTranslationCommand(Guid CourseId, string BaseLanguage, string Title, string? Description = null);

public sealed class CreateCourseHandler(IContentRepository repository, IValidator<CreateCourseCommand> validator)
{
    public async Task<Result<Guid>> HandleAsync(CreateCourseCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        ContentParsing.TryParseTargetLanguage(command.TargetLanguage, out var targetLanguage);
        ContentParsing.TryParseLevel(command.Level, out var level);
        var taken = Error.Conflict("course.code_taken", $"A course with code '{command.Code.Trim()}' already exists.");

        if (await repository.CourseCodeExistsAsync(command.Code.Trim(), cancellationToken))
            return taken;

        var course = new Course(command.Code, targetLanguage, level, command.ThumbnailUrl);
        repository.Add(course);

        return await repository.TrySaveAsync(taken, cancellationToken) is { } failure ? failure : course.Id;
    }
}

public sealed class UpdateCourseHandler(IContentRepository repository, IValidator<UpdateCourseCommand> validator)
{
    public async Task<Result> HandleAsync(UpdateCourseCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        var course = await repository.GetCourseAsync(command.CourseId, cancellationToken);
        if (course is null)
            return CommandSupport.CourseNotFound(command.CourseId);
        if (course.IsArchived)
            return CommandSupport.CourseArchived();

        ContentParsing.TryParseLevel(command.Level, out var level);
        if (!level.AppliesTo(course.TargetLanguage))
            return Error.Validation(nameof(command.Level), $"Level {command.Level.Trim()} does not belong to a {course.TargetLanguage.ToCode()} course.");

        course.UpdateDetails(level, command.ThumbnailUrl);

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed class PublishCourseHandler(IContentRepository repository)
{
    public async Task<Result> HandleAsync(PublishCourseCommand command, CancellationToken cancellationToken = default)
    {
        var course = await repository.GetCourseAsync(command.CourseId, cancellationToken);
        if (course is null)
            return CommandSupport.CourseNotFound(command.CourseId);

        if (course.Status is not (ContentStatus.Draft or ContentStatus.Review))
            return Error.Conflict("course.invalid_status", $"Only a draft or in-review course can be published; this one is {course.Status}.");

        // Publishing an empty course would show learners a course they can open and do nothing in.
        if (!await repository.HasPublishedLessonAsync(course.Id, cancellationToken))
            return Error.Conflict("course.no_published_lesson", "A course needs at least one published lesson before it can be published.");

        course.Publish();

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed class ArchiveCourseHandler(IContentRepository repository)
{
    public async Task<Result> HandleAsync(ArchiveCourseCommand command, CancellationToken cancellationToken = default)
    {
        var course = await repository.GetCourseAsync(command.CourseId, cancellationToken);
        if (course is null)
            return CommandSupport.CourseNotFound(command.CourseId);
        if (course.IsArchived)
            return Error.Conflict("course.already_archived", "The course is already archived.");

        course.Archive();

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed class AddCourseTranslationHandler(IContentRepository repository, IValidator<AddCourseTranslationCommand> validator)
{
    public async Task<Result> HandleAsync(AddCourseTranslationCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        var course = await repository.GetCourseAsync(command.CourseId, cancellationToken);
        if (course is null)
            return CommandSupport.CourseNotFound(command.CourseId);
        if (course.IsArchived)
            return CommandSupport.CourseArchived();

        ContentParsing.TryParseBaseLanguage(command.BaseLanguage, out var language);
        var exists = Error.Conflict("course.translation_exists", $"The course already has a '{language.ToCode()}' translation; update it instead.");
        if (course.Translations.Any(t => t.BaseLanguage == language))
            return exists;

        course.SetTranslation(language, command.Title, command.Description);

        return await repository.TrySaveAsync(exists, cancellationToken) is { } failure ? failure : Result.Success();
    }
}

public sealed class UpdateCourseTranslationHandler(IContentRepository repository, IValidator<UpdateCourseTranslationCommand> validator)
{
    public async Task<Result> HandleAsync(UpdateCourseTranslationCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        var course = await repository.GetCourseAsync(command.CourseId, cancellationToken);
        if (course is null)
            return CommandSupport.CourseNotFound(command.CourseId);
        if (course.IsArchived)
            return CommandSupport.CourseArchived();

        ContentParsing.TryParseBaseLanguage(command.BaseLanguage, out var language);
        if (course.Translations.All(t => t.BaseLanguage != language))
            return Error.NotFound("course.translation_not_found", $"The course has no '{language.ToCode()}' translation; add it first.");

        course.SetTranslation(language, command.Title, command.Description);

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
