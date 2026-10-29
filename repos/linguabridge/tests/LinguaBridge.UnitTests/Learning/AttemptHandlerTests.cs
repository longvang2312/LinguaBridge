using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Learning.Abstractions;
using LinguaBridge.Modules.Learning.Commands;
using LinguaBridge.Modules.Learning.Entities;
using LinguaBridge.Modules.Learning.Grading;

namespace LinguaBridge.UnitTests.Learning;

/// <summary>Shared setup: one learner, one clock, an in-memory repository and a catalogue of published lessons.</summary>
internal sealed class Harness
{
    public readonly FakeLearningRepository Repo = new();
    public readonly FakeLessonSource Source = new();
    public readonly FakeClock Clock = new();
    public readonly Guid User = Guid.NewGuid();

    public StartAttemptHandler Start => new(Repo, Source, new StartAttemptCommandValidator());

    public SubmitResponseHandler Submit => new(Repo, Source, Clock, new SubmitResponseCommandValidator());

    public CompleteAttemptHandler Complete => new(Repo, Source);

    public ReviewCardHandler Review => new(Repo, Clock, new ReviewCardCommandValidator());

    public async Task<Guid> StartAsync(LessonContent lesson, Guid? user = null)
    {
        var result = await Start.HandleAsync(new StartAttemptCommand(user ?? User, lesson.LessonId));
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value.AttemptId;
    }

    public Task<Result<ResponseResultDto>> AnswerAsync(Guid attemptId, SourceQuestion question, bool correct, Guid? user = null)
    {
        var choice = question.Options.Count > 0;
        var command = new SubmitResponseCommand(
            user ?? User,
            attemptId,
            question.Id,
            choice ? (correct ? LessonData.CorrectOption : LessonData.WrongOption) : null,
            choice ? null : (correct ? question.TargetText : "definitely wrong"),
            TimeSpentMs: 1500);
        return Submit.HandleAsync(command);
    }

    /// <summary>Starts an attempt, answers every gradable question (the first <paramref name="correctCount"/> of them correctly) and completes it.</summary>
    public async Task<AttemptCompletedDto> PlayAsync(LessonContent lesson, int? correctCount = null, Guid? user = null)
    {
        var attemptId = await StartAsync(lesson, user);
        var gradable = lesson.Questions.Where(QuestionGrading.IsGradable).ToList();
        for (var i = 0; i < gradable.Count; i++)
            Assert.True((await AnswerAsync(attemptId, gradable[i], i < (correctCount ?? gradable.Count), user)).IsSuccess);

        var completed = await Complete.HandleAsync(new CompleteAttemptCommand(user ?? User, attemptId));
        Assert.True(completed.IsSuccess, completed.Error?.Message);
        return completed.Value;
    }
}

public class AttemptHandlerTests
{
    private readonly Harness _h = new();

    // ---- Starting -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Starting_a_published_lesson_creates_an_attempt_and_counts_only_the_questions_that_will_be_scored()
    {
        var lesson = _h.Source.Publish(LessonData.Lesson([LessonData.Choice(0), LessonData.Typed(1), LessonData.Pronounce(2)]));

        var result = await _h.Start.HandleAsync(new StartAttemptCommand(_h.User, lesson.LessonId));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.TotalQuestions);
        var attempt = Assert.Single(_h.Repo.Attempts);
        Assert.Equal(result.Value.AttemptId, attempt.Id);
        Assert.Equal(_h.User, attempt.UserId);
        Assert.False(attempt.IsCompleted);
    }

    [Fact]
    public async Task A_lesson_that_is_unknown_or_no_longer_published_cannot_be_started()
    {
        var lesson = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()]));
        _h.Source.Unpublish(lesson.LessonId);

        var unpublished = await _h.Start.HandleAsync(new StartAttemptCommand(_h.User, lesson.LessonId));
        var unknown = await _h.Start.HandleAsync(new StartAttemptCommand(_h.User, Guid.NewGuid()));

        Assert.Equal("lesson.not_found", unpublished.Error!.Code);
        Assert.Equal("lesson.not_found", unknown.Error!.Code);
        Assert.Empty(_h.Repo.Attempts);
    }

    [Fact]
    public async Task Starting_needs_a_user_and_a_lesson()
    {
        var result = await _h.Start.HandleAsync(new StartAttemptCommand(Guid.Empty, Guid.Empty));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(_h.Repo.Attempts);
    }

    [Fact]
    public async Task Each_start_is_a_fresh_attempt()
    {
        var lesson = _h.Source.Publish(LessonData.Lesson([LessonData.Choice()]));

        await _h.StartAsync(lesson);
        await _h.StartAsync(lesson);

        Assert.Equal(2, _h.Repo.Attempts.Count);
    }

    // ---- Grading --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_correct_choice_is_marked_right_without_revealing_anything()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question, LessonData.Typed(1)]));
        var attemptId = await _h.StartAsync(lesson);

        var result = await _h.AnswerAsync(attemptId, question, correct: true);

        Assert.True(result.Value.IsCorrect);
        Assert.Null(result.Value.CorrectOptionId);
        Assert.Null(result.Value.CorrectAnswer);
        Assert.Null(result.Value.Explanation);
        Assert.Equal(1, result.Value.AnsweredCount);
        Assert.Equal(2, result.Value.TotalQuestions);
        var stored = Assert.Single(_h.Repo.Attempts.Single().Responses);
        Assert.True(stored.IsCorrect);
        Assert.Equal(LessonData.CorrectOption, stored.SelectedOptionId);
        Assert.Equal(1500, stored.TimeSpentMs);
    }

    [Fact]
    public async Task A_wrong_choice_is_marked_wrong_and_then_the_answer_and_explanation_are_shown()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));
        var attemptId = await _h.StartAsync(lesson);

        var result = await _h.AnswerAsync(attemptId, question, correct: false);

        Assert.False(result.Value.IsCorrect);
        Assert.Equal(LessonData.CorrectOption, result.Value.CorrectOptionId);
        Assert.Equal("Hello", result.Value.CorrectAnswer);
        Assert.Equal("Đây là lời chào.", result.Value.Explanation);
        Assert.Equal("Grammar note", result.Value.GrammarNotes);
    }

    [Fact]
    public async Task A_typed_answer_is_compared_forgivingly_and_a_wrong_one_reveals_the_target_text()
    {
        var question = LessonData.Typed(target: "Good morning");
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));
        var attemptId = await _h.StartAsync(lesson);

        var wrong = await _h.AnswerAsync(attemptId, question, correct: false);
        Assert.False(wrong.Value.IsCorrect);
        Assert.Equal("Good morning", wrong.Value.CorrectAnswer);

        var secondLesson = _h.Source.Publish(LessonData.Lesson([question with { Id = Guid.NewGuid() }]));
        var secondAttempt = await _h.StartAsync(secondLesson);
        var right = await _h.Submit.HandleAsync(new SubmitResponseCommand(
            _h.User, secondAttempt, secondLesson.Questions[0].Id, null, "  GOOD   morning! ", 900));
        Assert.True(right.Value.IsCorrect);
    }

    [Fact]
    public async Task A_choice_question_needs_an_option_that_belongs_to_it()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));
        var attemptId = await _h.StartAsync(lesson);

        var textOnly = await _h.Submit.HandleAsync(new SubmitResponseCommand(_h.User, attemptId, question.Id, null, "Hello", 100));
        var foreignOption = await _h.Submit.HandleAsync(new SubmitResponseCommand(_h.User, attemptId, question.Id, Guid.NewGuid(), null, 100));

        Assert.Contains("SelectedOptionId", textOnly.Error!.Details!.Keys);
        Assert.Contains("SelectedOptionId", foreignOption.Error!.Details!.Keys);
        Assert.Empty(_h.Repo.Attempts.Single().Responses);
    }

    [Fact]
    public async Task A_typed_question_needs_text()
    {
        var question = LessonData.Typed();
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));
        var attemptId = await _h.StartAsync(lesson);

        var optionOnly = await _h.Submit.HandleAsync(new SubmitResponseCommand(_h.User, attemptId, question.Id, Guid.NewGuid(), null, 100));
        var nothing = await _h.Submit.HandleAsync(new SubmitResponseCommand(_h.User, attemptId, question.Id, null, "  ", 100));

        Assert.Contains("UserInputText", optionOnly.Error!.Details!.Keys);
        Assert.Equal(ErrorType.Validation, nothing.Error!.Type);
        Assert.Empty(_h.Repo.Attempts.Single().Responses);
    }

    [Fact]
    public async Task Negative_time_and_oversized_answers_are_rejected()
    {
        var question = LessonData.Typed();
        var attemptId = await _h.StartAsync(_h.Source.Publish(LessonData.Lesson([question])));

        var negative = await _h.Submit.HandleAsync(new SubmitResponseCommand(_h.User, attemptId, question.Id, null, "x", -1));
        var huge = await _h.Submit.HandleAsync(new SubmitResponseCommand(_h.User, attemptId, question.Id, null, new string('x', SubmitResponseCommandValidator.MaxAnswerLength + 1), 10));

        Assert.Contains("TimeSpentMs", negative.Error!.Details!.Keys);
        Assert.Contains("UserInputText", huge.Error!.Details!.Keys);
    }

    [Fact]
    public async Task Questions_outside_the_quiz_cannot_be_answered()
    {
        var pronounce = LessonData.Pronounce();
        var lesson = _h.Source.Publish(LessonData.Lesson([LessonData.Choice(), pronounce]));
        var attemptId = await _h.StartAsync(lesson);

        var notServed = await _h.Submit.HandleAsync(new SubmitResponseCommand(_h.User, attemptId, pronounce.Id, null, "thank you", 100));
        var unknown = await _h.Submit.HandleAsync(new SubmitResponseCommand(_h.User, attemptId, Guid.NewGuid(), Guid.NewGuid(), null, 100));

        Assert.Equal("question.not_found", notServed.Error!.Code);
        Assert.Equal("question.not_found", unknown.Error!.Code);
        Assert.Empty(_h.Repo.Attempts.Single().Responses);
    }

    [Fact]
    public async Task A_question_can_be_answered_once_per_attempt()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));
        var attemptId = await _h.StartAsync(lesson);

        await _h.AnswerAsync(attemptId, question, correct: false);
        var second = await _h.AnswerAsync(attemptId, question, correct: true);

        Assert.Equal("response.already_answered", second.Error!.Code);
        Assert.False(Assert.Single(_h.Repo.Attempts.Single().Responses).IsCorrect);
    }

    [Fact]
    public async Task A_learner_cannot_answer_in_someone_elses_attempt_or_a_missing_one()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));
        var attemptId = await _h.StartAsync(lesson);
        var stranger = Guid.NewGuid();

        var theirs = await _h.AnswerAsync(attemptId, question, correct: true, user: stranger);
        var missing = await _h.AnswerAsync(Guid.NewGuid(), question, correct: true);

        Assert.Equal("attempt.not_found", theirs.Error!.Code);
        Assert.Equal("attempt.not_found", missing.Error!.Code);
        Assert.Empty(_h.Repo.Attempts.Single().Responses);
        Assert.Empty(_h.Repo.Cards);
    }

    [Fact]
    public async Task A_finished_attempt_accepts_no_more_answers()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question, LessonData.Choice(1)]));
        var attemptId = await _h.StartAsync(lesson);
        await _h.AnswerAsync(attemptId, question, correct: true);
        await _h.AnswerAsync(attemptId, lesson.Questions[1], correct: true);
        await _h.Complete.HandleAsync(new CompleteAttemptCommand(_h.User, attemptId));

        var late = await _h.AnswerAsync(attemptId, question, correct: true);

        Assert.Equal("attempt.already_completed", late.Error!.Code);
    }

    [Fact]
    public async Task Answering_stops_when_the_lesson_is_withdrawn_mid_attempt()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));
        var attemptId = await _h.StartAsync(lesson);
        _h.Source.Unpublish(lesson.LessonId);

        var result = await _h.AnswerAsync(attemptId, question, correct: true);

        Assert.Equal("lesson.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task A_lost_race_on_saving_is_reported_as_a_retryable_conflict()
    {
        var question = LessonData.Choice();
        var attemptId = await _h.StartAsync(_h.Source.Publish(LessonData.Lesson([question])));
        _h.Repo.FailNextSaveWithUniqueViolation = true;

        var result = await _h.AnswerAsync(attemptId, question, correct: true);

        Assert.Equal("request.concurrent_update", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    // ---- Spaced repetition fed by answers -----------------------------------------------------------------

    [Fact]
    public async Task A_first_correct_answer_creates_a_card_scheduled_with_a_good_rating()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question], language: TargetLanguage.Chinese));

        await _h.AnswerAsync(await _h.StartAsync(lesson), question, correct: true);

        var card = Assert.Single(_h.Repo.Cards);
        Assert.Equal(question.Id, card.VocabularyId);
        Assert.Equal(_h.User, card.UserId);
        Assert.Equal(TargetLanguage.Chinese, card.TargetLanguage);
        Assert.Equal(1, card.IntervalDays);
        Assert.Equal(1, card.Repetitions);
        Assert.Equal(_h.Clock.Today.AddDays(1), card.NextReviewDate);
        Assert.Equal(SrsState.Learning, card.State);
        var log = Assert.Single(_h.Repo.Logs);
        Assert.Equal(card.Id, log.UserVocabularyId);
        Assert.Equal(ReviewRating.Good, log.Rating);
        Assert.Equal((2.5, 2.5, 0, 1), (log.OldEaseFactor, log.NewEaseFactor, log.OldIntervalDays, log.NewIntervalDays));
    }

    [Fact]
    public async Task A_first_wrong_answer_creates_a_card_scheduled_as_a_lapse()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));

        await _h.AnswerAsync(await _h.StartAsync(lesson), question, correct: false);

        var card = Assert.Single(_h.Repo.Cards);
        Assert.Equal(0, card.Repetitions);
        Assert.Equal(1, card.IntervalDays);
        Assert.Equal(2.3, card.EaseFactor);
        Assert.Equal(ReviewRating.Again, Assert.Single(_h.Repo.Logs).Rating);
    }

    [Fact]
    public async Task Redoing_a_lesson_the_same_day_does_not_push_the_cards_further_out()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));

        for (var i = 0; i < 5; i++)
            await _h.AnswerAsync(await _h.StartAsync(lesson), question, correct: true);

        var card = Assert.Single(_h.Repo.Cards);
        Assert.Equal(1, card.IntervalDays);
        Assert.Equal(1, card.Repetitions);
        Assert.Single(_h.Repo.Logs);
    }

    [Fact]
    public async Task A_due_card_answered_correctly_moves_to_the_next_step()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));
        await _h.AnswerAsync(await _h.StartAsync(lesson), question, correct: true);

        _h.Clock.AdvanceDays(1);
        await _h.AnswerAsync(await _h.StartAsync(lesson), question, correct: true);

        var card = Assert.Single(_h.Repo.Cards);
        Assert.Equal(6, card.IntervalDays);
        Assert.Equal(2, card.Repetitions);
        Assert.Equal(_h.Clock.Today.AddDays(6), card.NextReviewDate);
        Assert.Equal(SrsState.Review, card.State);
        Assert.Equal(2, _h.Repo.Logs.Count);
    }

    [Fact]
    public async Task A_wrong_answer_on_a_card_that_is_not_due_still_counts_as_a_lapse()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));
        await _h.AnswerAsync(await _h.StartAsync(lesson), question, correct: true);
        var card = _h.Repo.Cards.Single();
        Assert.False(card.IsDueOn(_h.Clock.Today));

        await _h.AnswerAsync(await _h.StartAsync(lesson), question, correct: false);

        Assert.Equal(0, card.Repetitions);
        Assert.Equal(2.3, card.EaseFactor);
        Assert.Equal(2, _h.Repo.Logs.Count);
        Assert.Equal(ReviewRating.Again, _h.Repo.Logs[1].Rating);
    }

    [Fact]
    public async Task Each_learner_has_their_own_cards()
    {
        var question = LessonData.Choice();
        var lesson = _h.Source.Publish(LessonData.Lesson([question]));
        var other = Guid.NewGuid();

        await _h.AnswerAsync(await _h.StartAsync(lesson), question, correct: true);
        await _h.AnswerAsync(await _h.StartAsync(lesson, other), question, correct: false, user: other);

        Assert.Equal(2, _h.Repo.Cards.Count);
        Assert.Equal(1, _h.Repo.Cards.Single(c => c.UserId == _h.User).Repetitions);
        Assert.Equal(0, _h.Repo.Cards.Single(c => c.UserId == other).Repetitions);
    }
}
