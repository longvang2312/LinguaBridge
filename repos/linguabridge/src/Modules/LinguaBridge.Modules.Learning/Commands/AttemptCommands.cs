using FluentValidation;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Learning.Abstractions;
using LinguaBridge.Modules.Learning.Entities;
using LinguaBridge.Modules.Learning.Grading;

namespace LinguaBridge.Modules.Learning.Commands;

public sealed record StartAttemptCommand(Guid UserId, Guid LessonId);

/// <param name="SelectedOptionId">For questions with options.</param>
/// <param name="UserInputText">For questions answered by typing.</param>
/// <param name="BaseLanguage">Language of the explanation shown in the feedback.</param>
public sealed record SubmitResponseCommand(
    Guid UserId,
    Guid AttemptId,
    Guid QuestionId,
    Guid? SelectedOptionId,
    string? UserInputText,
    int TimeSpentMs,
    BaseLanguage BaseLanguage = BaseLanguage.Vietnamese);

public sealed record CompleteAttemptCommand(Guid UserId, Guid AttemptId);

public sealed class StartAttemptHandler(ILearningRepository repository, ILessonSource lessons, IValidator<StartAttemptCommand> validator)
{
    public async Task<Result<AttemptStartedDto>> HandleAsync(StartAttemptCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        // Only a published lesson of a published course can be started; anything else looks like it does not exist.
        var lesson = await lessons.GetPublishedLessonAsync(command.LessonId, BaseLanguage.Vietnamese, cancellationToken);
        if (lesson is null)
            return CommandSupport.LessonNotFound(command.LessonId);

        // Each start is a fresh attempt: an abandoned one stays incomplete and never counts as progress.
        var attempt = new ExerciseAttempt(command.UserId, lesson.LessonId);
        repository.Add(attempt);

        await repository.SaveChangesAsync(cancellationToken);
        return new AttemptStartedDto(attempt.Id, lesson.LessonId, QuestionGrading.Gradable(lesson).Count, attempt.StartedAt);
    }
}

/// <summary>
/// Grades one answer on the server, records it, and moves the learner's spaced-repetition card for that question.
/// <para>
/// Card rule: a correct answer counts as Good and a wrong one as Again. A new card is created either way. An
/// existing card is rescheduled only if it is due, or if the answer is wrong (a lapse always counts): re-answering
/// a card that is not due yet must not push its interval out, or redoing one lesson five times in a day would
/// schedule the cards months ahead.
/// </para>
/// </summary>
public sealed class SubmitResponseHandler(
    ILearningRepository repository,
    ILessonSource lessons,
    IDateTimeProvider clock,
    IValidator<SubmitResponseCommand> validator)
{
    public async Task<Result<ResponseResultDto>> HandleAsync(SubmitResponseCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        var attempt = await repository.GetAttemptAsync(command.AttemptId, command.UserId, cancellationToken);
        if (attempt is null)
            return CommandSupport.AttemptNotFound(command.AttemptId);
        if (attempt.IsCompleted)
            return Error.Conflict("attempt.already_completed", "The attempt is already completed.");

        var lesson = await lessons.GetPublishedLessonAsync(attempt.LessonId, command.BaseLanguage, cancellationToken);
        if (lesson is null)
            return CommandSupport.LessonNotFound(attempt.LessonId);

        var gradable = QuestionGrading.Gradable(lesson);
        var question = gradable.FirstOrDefault(q => q.Id == command.QuestionId);
        if (question is null)
            return Error.NotFound("question.not_found", $"Question {command.QuestionId} is not part of this lesson's quiz.");
        if (attempt.Responses.Any(r => r.QuestionId == question.Id))
            return Error.Conflict("response.already_answered", "This question has already been answered in this attempt.");

        var graded = Grade(question, command);
        if (graded.Failure is not null)
            return graded.Failure;

        attempt.AddResponse(question.Id, graded.IsCorrect, command.TimeSpentMs, command.SelectedOptionId, command.UserInputText);
        await UpdateCardAsync(command.UserId, question, lesson.TargetLanguage, graded.IsCorrect, cancellationToken);

        if (await repository.TrySaveAsync(cancellationToken) is { } conflict)
            return conflict;

        var feedback = graded.IsCorrect ? (OptionId: (Guid?)null, Text: (string?)null) : QuestionGrading.CorrectAnswer(question);
        return new ResponseResultDto(
            question.Id,
            graded.IsCorrect,
            feedback.OptionId,
            feedback.Text,
            graded.IsCorrect ? null : question.Explanation,
            graded.IsCorrect ? null : question.GrammarNotes,
            attempt.Responses.Count,
            gradable.Count);
    }

    private static (bool IsCorrect, Error? Failure) Grade(SourceQuestion question, SubmitResponseCommand command)
    {
        if (QuestionGrading.IsAnsweredByChoice(question))
        {
            if (command.SelectedOptionId is not { } optionId)
                return (false, Error.Validation(nameof(command.SelectedOptionId), "This question is answered by choosing an option: provide selectedOptionId."));

            var option = question.Options.FirstOrDefault(o => o.Id == optionId);
            return option is null
                ? (false, Error.Validation(nameof(command.SelectedOptionId), "selectedOptionId is not an option of this question."))
                : (option.IsCorrect, null);
        }

        if (string.IsNullOrWhiteSpace(command.UserInputText))
            return (false, Error.Validation(nameof(command.UserInputText), "This question is answered by typing: provide userInputText."));

        return (AnswerMatcher.Matches(command.UserInputText, question.TargetText), null);
    }

    private async Task UpdateCardAsync(Guid userId, SourceQuestion question, TargetLanguage targetLanguage, bool isCorrect, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var today = clock.TodayUtc();
        var rating = isCorrect ? ReviewRating.Good : ReviewRating.Again;

        var card = await repository.GetCardByVocabularyAsync(userId, question.Id, cancellationToken);
        if (card is null)
        {
            card = new UserVocabularyProgress(userId, question.Id, targetLanguage, today);
            repository.Add(card);
        }
        else if (!card.IsDueOn(today) && isCorrect)
        {
            return;
        }

        repository.Add(card.ApplyRating(rating, today, now));
    }
}

/// <summary>
/// Finishes an attempt: scores it, awards XP, and brings the learner's course progress up to date.
/// <list type="bullet">
/// <item>Every question of the quiz must have been answered; otherwise there is nothing fair to score.</item>
/// <item>Score is the share of questions answered correctly, rounded to a whole percent; a lesson with no
/// gradable questions (pure reading) scores 100.</item>
/// <item>XP is the lesson's reward scaled by the score, and only for the first completion of that lesson:
/// replaying for practice earns nothing, so XP cannot be farmed.</item>
/// <item>Course progress is recomputed from which lessons the learner has finished, not incremented, so a
/// repeated or concurrent completion cannot count a lesson twice.</item>
/// </list>
/// </summary>
public sealed class CompleteAttemptHandler(ILearningRepository repository, ILessonSource lessons)
{
    public async Task<Result<AttemptCompletedDto>> HandleAsync(CompleteAttemptCommand command, CancellationToken cancellationToken = default)
    {
        var attempt = await repository.GetAttemptAsync(command.AttemptId, command.UserId, cancellationToken);
        if (attempt is null)
            return CommandSupport.AttemptNotFound(command.AttemptId);
        if (attempt.IsCompleted)
            return Error.Conflict("attempt.already_completed", "The attempt is already completed.");

        var lesson = await lessons.GetPublishedLessonAsync(attempt.LessonId, BaseLanguage.Vietnamese, cancellationToken);
        if (lesson is null)
            return CommandSupport.LessonNotFound(attempt.LessonId);

        var gradable = QuestionGrading.Gradable(lesson);
        var firstAnswers = attempt.Responses
            .GroupBy(r => r.QuestionId)
            .ToDictionary(g => g.Key, g => g.First());

        var unanswered = gradable.Count(q => !firstAnswers.ContainsKey(q.Id));
        if (unanswered > 0)
            return Error.Conflict("attempt.unanswered_questions", $"{unanswered} question(s) of the lesson have not been answered yet.");

        var correct = gradable.Count(q => firstAnswers[q.Id].IsCorrect);
        var score = gradable.Count == 0 ? 100 : (int)Math.Round(100.0 * correct / gradable.Count, MidpointRounding.AwayFromZero);

        var isFirstCompletion = !await repository.HasCompletedLessonAsync(command.UserId, attempt.LessonId, attempt.Id, cancellationToken);
        var xp = isFirstCompletion ? (int)Math.Round(lesson.XpReward * score / 100.0, MidpointRounding.AwayFromZero) : 0;

        attempt.Complete(score, xp);

        var progress = await UpdateProgressAsync(command.UserId, lesson, isFirstCompletion, cancellationToken);
        if (progress is null)
            return CommandSupport.LessonNotFound(attempt.LessonId);

        if (await repository.TrySaveAsync(cancellationToken) is { } conflict)
            return conflict;

        return new AttemptCompletedDto(
            attempt.Id,
            score,
            correct,
            gradable.Count,
            xp,
            isFirstCompletion,
            new CourseProgressDto(progress.CourseId, progress.CompletedLessonsCount, progress.TotalLessonsCount, progress.IsCompleted, progress.LastAccessedAt));
    }

    private async Task<UserCourseProgress?> UpdateProgressAsync(Guid userId, LessonContent lesson, bool isFirstCompletion, CancellationToken cancellationToken)
    {
        var publishedLessonIds = await lessons.GetPublishedLessonIdsAsync(lesson.CourseId, cancellationToken);
        if (publishedLessonIds is null)
            return null;

        // The attempt being completed is not saved yet, so this counts the lessons finished before it.
        var finishedBefore = await repository.CountCompletedLessonsAsync(userId, publishedLessonIds, cancellationToken);
        var completed = finishedBefore + (isFirstCompletion && publishedLessonIds.Contains(lesson.LessonId) ? 1 : 0);

        var progress = await repository.GetCourseProgressAsync(userId, lesson.CourseId, cancellationToken);
        if (progress is null)
        {
            progress = new UserCourseProgress(userId, lesson.CourseId, publishedLessonIds.Count);
            repository.Add(progress);
        }

        progress.Update(completed, publishedLessonIds.Count);
        return progress;
    }
}
