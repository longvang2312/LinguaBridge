using System.Text.Json;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Learning.Abstractions;
using LinguaBridge.Modules.Learning.Commands;
using LinguaBridge.Modules.Learning.Entities;
using LinguaBridge.Modules.Learning.Queries;

namespace LinguaBridge.UnitTests.Learning;

public class CompletionReviewAndQueryTests
{
    private readonly Harness _h = new();

    private LessonContent FourQuestionLesson(Guid? courseId = null, int xp = 20) =>
        _h.Source.Publish(LessonData.Lesson([LessonData.Choice(0), LessonData.Choice(1), LessonData.Typed(2), LessonData.Typed(3)], courseId, xp));

    // ---- Completing: score and XP --------------------------------------------------------------------------

    [Fact]
    public async Task The_score_is_the_share_answered_correctly_and_xp_scales_with_it()
    {
        var lesson = FourQuestionLesson(xp: 20);

        var result = await _h.PlayAsync(lesson, correctCount: 3);

        Assert.Equal(75, result.ScorePercentage);
        Assert.Equal(3, result.CorrectCount);
        Assert.Equal(4, result.TotalQuestions);
        Assert.Equal(15, result.XpEarned);
        Assert.True(result.IsFirstCompletion);
        var attempt = _h.Repo.Attempts.Single();
        Assert.True(attempt.IsCompleted);
        Assert.Equal(75, attempt.ScorePercentage);
        Assert.Equal(15, attempt.XpEarned);
    }

    [Fact]
    public async Task A_perfect_lesson_earns_the_full_reward()
    {
        var result = await _h.PlayAsync(FourQuestionLesson(xp: 30));

        Assert.Equal(100, result.ScorePercentage);
        Assert.Equal(30, result.XpEarned);
    }

    [Theory]
    [InlineData(1, 3, 33)]
    [InlineData(2, 3, 67)]
    [InlineData(1, 8, 13)]
    [InlineData(0, 3, 0)]
    public async Task The_score_is_rounded_to_a_whole_percent(int correct, int total, int expected)
    {
        var lesson = _h.Source.Publish(LessonData.Lesson(Enumerable.Range(0, total).Select(i => LessonData.Choice(i)).ToList()));

        var result = await _h.PlayAsync(lesson, correct);

        Assert.Equal(expected, result.ScorePercentage);
    }

    [Fact]
    public async Task A_lesson_with_nothing_to_score_still_completes_with_a_full_score()
    {
        var reading = _h.Source.Publish(LessonData.Lesson([LessonData.Pronounce()], xp: 10));

        var result = await _h.PlayAsync(reading);

        Assert.Equal(100, result.ScorePercentage);
        Assert.Equal(0, result.TotalQuestions);
        Assert.Equal(10, result.XpEarned);
    }

    [Fact]
    public async Task Questions_that_are_not_served_do_not_count_against_the_learner()
    {
        var lesson = _h.Source.Publish(LessonData.Lesson([LessonData.Choice(0), LessonData.Pronounce(1), LessonData.Typed(2, type: "MultipleChoice")]));

        var result = await _h.PlayAsync(lesson);

        Assert.Equal(1, result.TotalQuestions);
        Assert.Equal(100, result.ScorePercentage);
    }

    [Fact]
    public async Task Replaying_a_lesson_earns_no_further_xp()
    {
        var lesson = FourQuestionLesson(xp: 20);

        var first = await _h.PlayAsync(lesson);
        var replay = await _h.PlayAsync(lesson);

        Assert.Equal(20, first.XpEarned);
        Assert.False(replay.IsFirstCompletion);
        Assert.Equal(0, replay.XpEarned);
        Assert.Equal(100, replay.ScorePercentage);
    }

    [Fact]
    public async Task Another_learners_completion_does_not_make_mine_a_replay()
    {
        var lesson = FourQuestionLesson(xp: 20);

        await _h.PlayAsync(lesson, user: Guid.NewGuid());
        var mine = await _h.PlayAsync(lesson);

        Assert.True(mine.IsFirstCompletion);
        Assert.Equal(20, mine.XpEarned);
    }

    // ---- Completing: rules ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_lesson_cannot_be_finished_with_questions_unanswered()
    {
        var lesson = FourQuestionLesson();
        var attemptId = await _h.StartAsync(lesson);
        await _h.AnswerAsync(attemptId, lesson.Questions[0], correct: true);

        var result = await _h.Complete.HandleAsync(new CompleteAttemptCommand(_h.User, attemptId));

        Assert.Equal("attempt.unanswered_questions", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.False(_h.Repo.Attempts.Single().IsCompleted);
        Assert.Empty(_h.Repo.Progress);
    }

    [Fact]
    public async Task An_attempt_completes_only_once()
    {
        var lesson = FourQuestionLesson();
        var attemptId = await _h.StartAsync(lesson);
        foreach (var question in lesson.Questions)
            await _h.AnswerAsync(attemptId, question, correct: true);
        await _h.Complete.HandleAsync(new CompleteAttemptCommand(_h.User, attemptId));

        var again = await _h.Complete.HandleAsync(new CompleteAttemptCommand(_h.User, attemptId));

        Assert.Equal("attempt.already_completed", again.Error!.Code);
    }

    [Fact]
    public async Task A_learner_cannot_complete_someone_elses_attempt_or_a_missing_one()
    {
        var lesson = FourQuestionLesson();
        var attemptId = await _h.StartAsync(lesson);

        var theirs = await _h.Complete.HandleAsync(new CompleteAttemptCommand(Guid.NewGuid(), attemptId));
        var missing = await _h.Complete.HandleAsync(new CompleteAttemptCommand(_h.User, Guid.NewGuid()));

        Assert.Equal("attempt.not_found", theirs.Error!.Code);
        Assert.Equal("attempt.not_found", missing.Error!.Code);
    }

    [Fact]
    public async Task Completing_fails_when_the_lesson_has_been_withdrawn()
    {
        var lesson = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()]));
        var attemptId = await _h.StartAsync(lesson);
        _h.Source.Unpublish(lesson.LessonId);

        var result = await _h.Complete.HandleAsync(new CompleteAttemptCommand(_h.User, attemptId));

        Assert.Equal("lesson.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task A_concurrent_completion_that_collides_on_saving_is_reported_as_a_retryable_conflict()
    {
        var lesson = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()]));
        var attemptId = await _h.StartAsync(lesson);
        await _h.AnswerAsync(attemptId, lesson.Questions[0], correct: true);
        _h.Repo.FailNextSaveWithUniqueViolation = true;

        var result = await _h.Complete.HandleAsync(new CompleteAttemptCommand(_h.User, attemptId));

        Assert.Equal("request.concurrent_update", result.Error!.Code);
    }

    // ---- Course progress -------------------------------------------------------------------------------------

    [Fact]
    public async Task Finishing_lessons_moves_course_progress_forward_until_the_course_is_complete()
    {
        var courseId = Guid.NewGuid();
        var lessons = Enumerable.Range(0, 3).Select(_ => _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId))).ToList();

        var first = await _h.PlayAsync(lessons[0]);
        var second = await _h.PlayAsync(lessons[1]);
        var third = await _h.PlayAsync(lessons[2]);

        Assert.Equal((1, 3, false), (first.Progress.CompletedLessonsCount, first.Progress.TotalLessonsCount, first.Progress.IsCompleted));
        Assert.Equal((2, 3, false), (second.Progress.CompletedLessonsCount, second.Progress.TotalLessonsCount, second.Progress.IsCompleted));
        Assert.Equal((3, 3, true), (third.Progress.CompletedLessonsCount, third.Progress.TotalLessonsCount, third.Progress.IsCompleted));
        Assert.Single(_h.Repo.Progress);
    }

    [Fact]
    public async Task Replaying_a_lesson_does_not_count_it_twice()
    {
        var courseId = Guid.NewGuid();
        var lesson = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));
        _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));

        await _h.PlayAsync(lesson);
        var replay = await _h.PlayAsync(lesson);

        Assert.Equal(1, replay.Progress.CompletedLessonsCount);
        Assert.Equal(2, replay.Progress.TotalLessonsCount);
    }

    [Fact]
    public async Task Progress_is_per_learner()
    {
        var courseId = Guid.NewGuid();
        var lesson = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));
        var other = Guid.NewGuid();

        await _h.PlayAsync(lesson, user: other);
        var mine = await _h.PlayAsync(lesson);

        Assert.Equal(1, mine.Progress.CompletedLessonsCount);
        Assert.Equal(2, _h.Repo.Progress.Count);
    }

    [Fact]
    public async Task The_total_follows_lessons_published_after_the_course_was_started()
    {
        var courseId = Guid.NewGuid();
        var first = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));
        var second = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));
        await _h.PlayAsync(first);

        _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));
        var result = await _h.PlayAsync(second);

        Assert.Equal(2, result.Progress.CompletedLessonsCount);
        Assert.Equal(3, result.Progress.TotalLessonsCount);
        Assert.False(result.Progress.IsCompleted);
    }

    [Fact]
    public async Task A_lesson_that_is_withdrawn_later_no_longer_counts_toward_the_course()
    {
        var courseId = Guid.NewGuid();
        var withdrawn = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));
        var kept = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));
        var other = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));
        await _h.PlayAsync(withdrawn);
        _h.Source.Unpublish(withdrawn.LessonId);

        var result = await _h.PlayAsync(kept);

        Assert.Equal(1, result.Progress.CompletedLessonsCount);
        Assert.Equal(2, result.Progress.TotalLessonsCount);
        Assert.NotNull(other);
    }

    // ---- Reviewing cards ---------------------------------------------------------------------------------------

    private UserVocabularyProgress DueCard(int dueDaysAgo = 0, Guid? user = null, Action<UserVocabularyProgress>? configure = null)
    {
        var card = new UserVocabularyProgress(user ?? _h.User, Guid.NewGuid(), TargetLanguage.English, _h.Clock.Today.AddDays(-dueDaysAgo));
        configure?.Invoke(card);
        _h.Repo.Cards.Add(card);
        return card;
    }

    [Fact]
    public async Task Rating_a_due_card_schedules_it_with_sm2_and_logs_the_change()
    {
        var card = DueCard();

        var result = await _h.Review.HandleAsync(new ReviewCardCommand(_h.User, card.Id, 3));

        Assert.True(result.IsSuccess);
        Assert.Equal((card.Id, 3, 1, 1, "Learning"), (result.Value.CardId, result.Value.Rating, result.Value.IntervalDays, result.Value.Repetitions, result.Value.State));
        Assert.Equal(_h.Clock.Today.AddDays(1), result.Value.NextReviewDate);
        Assert.Equal(_h.Clock.Today.AddDays(1), card.NextReviewDate);
        Assert.NotNull(card.LastReviewDate);

        var log = Assert.Single(_h.Repo.Logs);
        Assert.Equal((card.Id, ReviewRating.Good, 2.5, 2.5, 0, 1), (log.UserVocabularyId, log.Rating, log.OldEaseFactor, log.NewEaseFactor, log.OldIntervalDays, log.NewIntervalDays));
    }

    [Theory]
    [InlineData(1, 2.3, 1, 0)]
    [InlineData(2, 2.35, 1, 0)]
    [InlineData(3, 2.5, 1, 1)]
    [InlineData(4, 2.65, 2, 1)]
    public async Task Each_rating_has_its_own_effect_on_a_new_card(int rating, double ease, int interval, int repetitions)
    {
        var card = DueCard();

        var result = await _h.Review.HandleAsync(new ReviewCardCommand(_h.User, card.Id, rating));

        Assert.Equal((ease, interval, repetitions), (result.Value.EaseFactor, result.Value.IntervalDays, result.Value.Repetitions));
    }

    [Fact]
    public async Task An_overdue_card_is_scheduled_from_today_not_from_its_missed_date()
    {
        var card = DueCard(dueDaysAgo: 10, configure: c => c.ApplyReview(new LinguaBridge.Modules.Learning.Srs.Sm2Result(2.5, 6, 2, _h.Clock.Today.AddDays(-10), SrsState.Review), _h.Clock.UtcNow.AddDays(-16)));

        var result = await _h.Review.HandleAsync(new ReviewCardCommand(_h.User, card.Id, 3));

        Assert.Equal(15, result.Value.IntervalDays);
        Assert.Equal(_h.Clock.Today.AddDays(15), result.Value.NextReviewDate);
    }

    [Fact]
    public async Task A_card_that_is_not_due_cannot_be_reviewed_early()
    {
        var card = DueCard(dueDaysAgo: -3);

        var result = await _h.Review.HandleAsync(new ReviewCardCommand(_h.User, card.Id, 4));

        Assert.Equal("srs.card_not_due", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Contains(_h.Clock.Today.AddDays(3).ToString("yyyy-MM-dd"), result.Error.Message);
        Assert.Empty(_h.Repo.Logs);
        Assert.Equal(0, card.IntervalDays);
    }

    [Fact]
    public async Task A_card_reviewed_today_is_not_due_again_today()
    {
        var card = DueCard();
        await _h.Review.HandleAsync(new ReviewCardCommand(_h.User, card.Id, 3));

        var again = await _h.Review.HandleAsync(new ReviewCardCommand(_h.User, card.Id, 3));

        Assert.Equal("srs.card_not_due", again.Error!.Code);
        Assert.Single(_h.Repo.Logs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public async Task Ratings_outside_one_to_four_are_rejected(int rating)
    {
        var card = DueCard();

        var result = await _h.Review.HandleAsync(new ReviewCardCommand(_h.User, card.Id, rating));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Contains("Rating", result.Error.Details!.Keys);
        Assert.Empty(_h.Repo.Logs);
    }

    [Fact]
    public async Task A_learner_cannot_review_someone_elses_card_or_a_missing_one()
    {
        var card = DueCard(user: Guid.NewGuid());

        var theirs = await _h.Review.HandleAsync(new ReviewCardCommand(_h.User, card.Id, 3));
        var missing = await _h.Review.HandleAsync(new ReviewCardCommand(_h.User, Guid.NewGuid(), 3));

        Assert.Equal("card.not_found", theirs.Error!.Code);
        Assert.Equal("card.not_found", missing.Error!.Code);
        Assert.Empty(_h.Repo.Logs);
    }

    [Fact]
    public async Task A_lost_race_while_saving_a_review_is_a_retryable_conflict()
    {
        var card = DueCard();
        _h.Repo.FailNextSaveWithUniqueViolation = true;

        var result = await _h.Review.HandleAsync(new ReviewCardCommand(_h.User, card.Id, 3));

        Assert.Equal("request.concurrent_update", result.Error!.Code);
    }

    // ---- Queries -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_quiz_lists_gradable_questions_in_order_without_an_answer_key()
    {
        var lesson = _h.Source.Publish(LessonData.Lesson([LessonData.Typed(2), LessonData.Pronounce(1), LessonData.Choice(0)], xp: 25));

        var result = await new GetQuizHandler(_h.Source).HandleAsync(new GetQuizQuery(lesson.LessonId, BaseLanguage.Vietnamese));

        Assert.Equal(25, result.Value.XpReward);
        Assert.Equal([0, 2], result.Value.Questions.Select(q => q.OrderIndex));
        Assert.DoesNotContain("correct", JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Value.Questions[1].TargetText);
    }

    [Fact]
    public async Task The_quiz_of_a_missing_or_unpublished_lesson_is_not_found()
    {
        var result = await new GetQuizHandler(_h.Source).HandleAsync(new GetQuizQuery(Guid.NewGuid(), BaseLanguage.Vietnamese));

        Assert.Equal("lesson.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task The_due_list_is_asked_for_today_the_learner_and_a_clamped_page_size()
    {
        var readModel = new FakeLearningReadModel();
        var handler = new GetDueCardsHandler(readModel, _h.Clock);

        await handler.HandleAsync(new GetDueCardsQuery(_h.User, BaseLanguage.English, TargetLanguage.Chinese));
        await handler.HandleAsync(new GetDueCardsQuery(_h.User, BaseLanguage.Vietnamese, null, Limit: 0));
        await handler.HandleAsync(new GetDueCardsQuery(_h.User, BaseLanguage.Vietnamese, null, Limit: 5000));

        Assert.Equal(
            [
                (_h.User, _h.Clock.Today, BaseLanguage.English, (TargetLanguage?)TargetLanguage.Chinese, 20),
                (_h.User, _h.Clock.Today, BaseLanguage.Vietnamese, null, 1),
                (_h.User, _h.Clock.Today, BaseLanguage.Vietnamese, null, 100),
            ],
            readModel.Calls);
    }

    [Fact]
    public async Task Today_is_the_utc_date()
    {
        var readModel = new FakeLearningReadModel();
        _h.Clock.UtcNow = new DateTimeOffset(2026, 10, 29, 23, 59, 0, TimeSpan.Zero);

        await new GetDueCardsHandler(readModel, _h.Clock).HandleAsync(new GetDueCardsQuery(_h.User, BaseLanguage.Vietnamese, null));

        Assert.Equal(new DateOnly(2026, 10, 29), readModel.Calls.Single().Today);
    }

    [Fact]
    public async Task Progress_of_an_unknown_or_unpublished_course_is_not_found()
    {
        var result = await new GetCourseProgressHandler(_h.Repo, _h.Source).HandleAsync(new GetCourseProgressQuery(_h.User, Guid.NewGuid()));

        Assert.Equal("course.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Progress_before_any_lesson_is_zero_of_the_published_lessons()
    {
        var courseId = Guid.NewGuid();
        _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));
        _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));

        var result = await new GetCourseProgressHandler(_h.Repo, _h.Source).HandleAsync(new GetCourseProgressQuery(_h.User, courseId));

        Assert.Equal((0, 2, false), (result.Value.CompletedLessonsCount, result.Value.TotalLessonsCount, result.Value.IsCompleted));
        Assert.Null(result.Value.LastAccessedAt);
    }

    [Fact]
    public async Task Progress_reports_what_the_learner_has_finished_against_todays_lesson_count()
    {
        var courseId = Guid.NewGuid();
        var first = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));
        _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId));
        await _h.PlayAsync(first);

        var result = await new GetCourseProgressHandler(_h.Repo, _h.Source).HandleAsync(new GetCourseProgressQuery(_h.User, courseId));

        Assert.Equal((1, 2, false), (result.Value.CompletedLessonsCount, result.Value.TotalLessonsCount, result.Value.IsCompleted));
        Assert.NotNull(result.Value.LastAccessedAt);
    }

    [Fact]
    public async Task Progress_never_shows_more_completed_than_there_are_lessons()
    {
        var courseId = Guid.NewGuid();
        var lessons = Enumerable.Range(0, 3).Select(_ => _h.Source.Publish(LessonData.Lesson([LessonData.Choice()], courseId))).ToList();
        foreach (var lesson in lessons)
            await _h.PlayAsync(lesson);
        _h.Source.Unpublish(lessons[0].LessonId);
        _h.Source.Unpublish(lessons[1].LessonId);

        var result = await new GetCourseProgressHandler(_h.Repo, _h.Source).HandleAsync(new GetCourseProgressQuery(_h.User, courseId));

        Assert.Equal((1, 1, true), (result.Value.CompletedLessonsCount, result.Value.TotalLessonsCount, result.Value.IsCompleted));
    }

    // ---- Entities ----------------------------------------------------------------------------------------------

    [Fact]
    public void Progress_update_caps_completed_at_the_total_and_is_idempotent()
    {
        var progress = new UserCourseProgress(Guid.NewGuid(), Guid.NewGuid(), 3);

        progress.Update(5, 3);
        progress.Update(5, 3);

        Assert.Equal(3, progress.CompletedLessonsCount);
        Assert.True(progress.IsCompleted);
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Update(-1, 3));
    }

    [Fact]
    public void An_attempt_rejects_a_second_answer_to_the_same_question()
    {
        var attempt = new ExerciseAttempt(Guid.NewGuid(), Guid.NewGuid());
        var questionId = Guid.NewGuid();
        attempt.AddResponse(questionId, true, 100);

        Assert.Throws<InvalidOperationException>(() => attempt.AddResponse(questionId, false, 100));
    }
}
