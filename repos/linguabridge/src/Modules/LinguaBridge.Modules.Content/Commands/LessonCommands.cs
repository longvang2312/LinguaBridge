using FluentValidation;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content.Abstractions;
using LinguaBridge.Modules.Content.Entities;

namespace LinguaBridge.Modules.Content.Commands;

/// <param name="OrderIndex">Position in the course; null appends after the last lesson.</param>
/// <param name="LessonType"><c>Standard</c>, <c>Vocabulary</c>, <c>Pinyin</c> or <c>Grammar</c>.</param>
/// <param name="XpReward">Null uses the default.</param>
public sealed record CreateLessonCommand(Guid CourseId, string LessonType, int? OrderIndex = null, int? XpReward = null);

public sealed record PublishLessonCommand(Guid LessonId);

public sealed record AddLessonTranslationCommand(Guid LessonId, string BaseLanguage, string Title, string? GuideText = null);

public sealed class CreateLessonHandler(IContentRepository repository, IValidator<CreateLessonCommand> validator)
{
    public async Task<Result<Guid>> HandleAsync(CreateLessonCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        var course = await repository.GetCourseAsync(command.CourseId, cancellationToken);
        if (course is null)
            return CommandSupport.CourseNotFound(command.CourseId);
        if (course.IsArchived)
            return CommandSupport.CourseArchived();

        ContentParsing.TryParseLessonType(command.LessonType, out var lessonType);
        var orderIndex = command.OrderIndex ?? await repository.GetNextLessonOrderIndexAsync(course.Id, cancellationToken);

        // Always a draft: nothing a learner can see changes until the lesson is published on purpose.
        var lesson = new Lesson(course.Id, orderIndex, lessonType, command.XpReward ?? Lesson.DefaultXpReward);
        repository.Add(lesson);

        await repository.SaveChangesAsync(cancellationToken);
        return lesson.Id;
    }
}

public sealed class PublishLessonHandler(IContentRepository repository)
{
    public async Task<Result> HandleAsync(PublishLessonCommand command, CancellationToken cancellationToken = default)
    {
        var lesson = await repository.GetLessonAsync(command.LessonId, cancellationToken);
        if (lesson is null)
            return CommandSupport.LessonNotFound(command.LessonId);

        var course = await repository.GetCourseAsync(lesson.CourseId, cancellationToken);
        if (course is null)
            return CommandSupport.CourseNotFound(lesson.CourseId);
        if (course.IsArchived)
            return CommandSupport.CourseArchived();

        if (lesson.Status is not (ContentStatus.Draft or ContentStatus.Review))
            return Error.Conflict("lesson.invalid_status", $"Only a draft or in-review lesson can be published; this one is {lesson.Status}.");

        lesson.Publish();

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed class AddLessonTranslationHandler(IContentRepository repository, IValidator<AddLessonTranslationCommand> validator)
{
    public async Task<Result> HandleAsync(AddLessonTranslationCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        var lesson = await repository.GetLessonAsync(command.LessonId, cancellationToken);
        if (lesson is null)
            return CommandSupport.LessonNotFound(command.LessonId);

        ContentParsing.TryParseBaseLanguage(command.BaseLanguage, out var language);
        var exists = Error.Conflict("lesson.translation_exists", $"The lesson already has a '{language.ToCode()}' translation.");
        if (lesson.Translations.Any(t => t.BaseLanguage == language))
            return exists;

        lesson.SetTranslation(language, command.Title, command.GuideText);

        return await repository.TrySaveAsync(exists, cancellationToken) is { } failure ? failure : Result.Success();
    }
}
