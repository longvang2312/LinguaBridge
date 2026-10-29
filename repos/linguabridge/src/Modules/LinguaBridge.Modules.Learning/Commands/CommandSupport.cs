using FluentValidation;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Learning.Abstractions;
using LinguaBridge.Modules.Learning.Entities;
using LinguaBridge.Modules.Learning.Srs;

namespace LinguaBridge.Modules.Learning.Commands;

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

    /// <summary>Saves, turning a lost race on a unique key (a concurrent identical request won) into a conflict to retry.</summary>
    public static async Task<Error?> TrySaveAsync(this ILearningRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            await repository.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (UniqueConstraintViolationException)
        {
            return Error.Conflict("request.concurrent_update", "The same data was changed by another request at the same moment. Please retry.");
        }
    }

    public static DateOnly TodayUtc(this IDateTimeProvider clock) => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    public static Error LessonNotFound(Guid id) => Error.NotFound("lesson.not_found", $"Lesson {id} was not found or is not available.");

    public static Error AttemptNotFound(Guid id) => Error.NotFound("attempt.not_found", $"Attempt {id} was not found.");

    /// <summary>
    /// Runs one SM-2 step on the card and returns the audit record for it. The caller adds the log to the repository.
    /// </summary>
    public static ReviewLog ApplyRating(this UserVocabularyProgress card, ReviewRating rating, DateOnly today, DateTimeOffset now)
    {
        var easeBefore = card.EaseFactor;
        var intervalBefore = card.IntervalDays;

        var result = Sm2Engine.Calculate(new Sm2Input(card.EaseFactor, card.IntervalDays, card.Repetitions, rating, today));
        card.ApplyReview(result, now);

        return new ReviewLog(card.Id, rating, easeBefore, result.EaseFactor, intervalBefore, result.IntervalDays);
    }
}
