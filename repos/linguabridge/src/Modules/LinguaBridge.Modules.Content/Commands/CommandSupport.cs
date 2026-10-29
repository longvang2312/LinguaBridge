using FluentValidation;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Content.Abstractions;

namespace LinguaBridge.Modules.Content.Commands;

internal static class CommandSupport
{
    /// <summary>Runs the validator; returns the failure to hand back, or null when the command is valid.</summary>
    public static async Task<Error?> CheckAsync<T>(this IValidator<T> validator, T command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (validation.IsValid)
            return null;

        var details = validation.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
        return Error.Validation(details);
    }

    /// <summary>
    /// Saves, turning a lost race on a unique key (another request created the same thing a moment ago)
    /// into the same conflict the up-front check would have reported.
    /// </summary>
    public static async Task<Error?> TrySaveAsync(this IContentRepository repository, Error conflictOnDuplicate, CancellationToken cancellationToken)
    {
        try
        {
            await repository.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (UniqueConstraintViolationException)
        {
            return conflictOnDuplicate;
        }
    }

    public static Error CourseNotFound(Guid id) => Error.NotFound("course.not_found", $"Course {id} was not found.");

    public static Error LessonNotFound(Guid id) => Error.NotFound("lesson.not_found", $"Lesson {id} was not found.");

    public static Error QuestionNotFound(Guid id) => Error.NotFound("question.not_found", $"Question {id} was not found.");

    public static Error CourseArchived() => Error.Conflict("course.archived", "The course is archived and can no longer be changed.");
}
