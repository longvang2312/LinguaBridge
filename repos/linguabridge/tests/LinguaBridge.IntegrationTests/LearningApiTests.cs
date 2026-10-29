using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Infrastructure.Persistence;
using LinguaBridge.Modules.Learning.Entities;
using LinguaBridge.Modules.Learning.Srs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaBridge.IntegrationTests;

/// <summary>
/// The learner API end to end on a real PostgreSQL: content is authored through the admin API, then learners take
/// quizzes, answer, finish lessons and review cards. Each test uses its own learner (and its own course), so the
/// shared database never couples tests together.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LearningApiTests(ContentApiFixture api) : IClassFixture<ContentApiFixture>
{
    private const string Learning = "/api/v1/learning";
    private const string ContentApi = "/api/content";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // ---- Access -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        using var client = api.Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{Learning}/srs/due")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"{Learning}/attempts", new { lessonId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task A_token_that_belongs_to_a_service_and_not_a_learner_is_refused()
    {
        using var service = api.WithSubject("internal-service");

        foreach (var response in new[]
        {
            await service.GetAsync($"{Learning}/srs/due"),
            await service.GetAsync($"{Learning}/progress/{Guid.NewGuid()}"),
            await service.GetAsync($"{Learning}/lessons/{Guid.NewGuid()}/quiz"),
            await service.PostAsJsonAsync($"{Learning}/attempts", new { lessonId = Guid.NewGuid() }),
            await service.PostAsJsonAsync($"{Learning}/srs/review", new { cardId = Guid.NewGuid(), rating = 3 }),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("learner.required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    // ---- The quiz never reveals the answers ---------------------------------------------------------------

    [Fact]
    public async Task The_quiz_carries_no_correctness_flag_although_the_answers_exist_on_the_server()
    {
        using var admin = api.Admin();
        using var learner = api.LearnerAs(Guid.NewGuid());
        var lesson = await Seed.PublishedLessonAsync(admin);

        var response = await learner.GetAsync($"{Learning}/lessons/{lesson.LessonId}/quiz?baseLanguage=vi");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("correct", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(lesson.TypedAnswer, body, StringComparison.OrdinalIgnoreCase);

        // The same data does carry the flag for an admin, so its absence above is the quiz's doing.
        var preview = await admin.GetStringAsync($"{ContentApi}/lessons/{lesson.LessonId}/preview");
        Assert.Contains("isCorrect", preview);
    }

    [Fact]
    public async Task The_quiz_shows_choices_but_hides_the_text_of_typed_questions()
    {
        using var admin = api.Admin();
        using var learner = api.LearnerAs(Guid.NewGuid());
        var lesson = await Seed.PublishedLessonAsync(admin);

        var quiz = await GetJsonAsync(learner, $"{Learning}/lessons/{lesson.LessonId}/quiz?baseLanguage=vi");

        Assert.Equal(lesson.CourseId, quiz.GetProperty("courseId").GetGuid());
        Assert.Equal(20, quiz.GetProperty("xpReward").GetInt32());
        var questions = quiz.GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal(2, questions.Count);

        var choice = questions.Single(q => q.GetProperty("id").GetGuid() == lesson.ChoiceQuestionId);
        Assert.Equal("你好", choice.GetProperty("targetText").GetString());
        Assert.Equal(3, choice.GetProperty("options").GetArrayLength());

        var typed = questions.Single(q => q.GetProperty("id").GetGuid() == lesson.TypedQuestionId);
        Assert.Equal(JsonValueKind.Null, typed.GetProperty("targetText").ValueKind);
        Assert.Equal("Chào buổi sáng", typed.GetProperty("promptTranslation").GetString());
    }

    [Fact]
    public async Task Pronunciation_questions_are_not_served_yet()
    {
        using var admin = api.Admin();
        using var learner = api.LearnerAs(Guid.NewGuid());
        var lesson = await Seed.PublishedLessonAsync(admin);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"{ContentApi}/lessons/{lesson.LessonId}/questions", new { questionType = "Pronounce", targetText = "Thank you" })).StatusCode);

        var quiz = await GetJsonAsync(learner, $"{Learning}/lessons/{lesson.LessonId}/quiz");

        Assert.Equal(2, quiz.GetProperty("questions").GetArrayLength());
    }

    [Fact]
    public async Task Lessons_that_are_not_published_do_not_exist_for_learners()
    {
        using var admin = api.Admin();
        using var learner = api.LearnerAs(Guid.NewGuid());
        var courseId = await Seed.CourseAsync(admin);
        var draftLesson = await Seed.LessonAsync(admin, courseId, publish: false);
        var inUnpublishedCourse = (await Seed.LessonAsync(admin, courseId, publish: true)).LessonId;

        Assert.Equal(HttpStatusCode.NotFound, (await learner.GetAsync($"{Learning}/lessons/{draftLesson.LessonId}/quiz")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await learner.GetAsync($"{Learning}/lessons/{inUnpublishedCourse}/quiz")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await learner.GetAsync($"{Learning}/lessons/{Guid.NewGuid()}/quiz")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await learner.PostAsJsonAsync($"{Learning}/attempts", new { lessonId = draftLesson.LessonId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await learner.PostAsJsonAsync($"{Learning}/attempts", new { lessonId = inUnpublishedCourse })).StatusCode);
    }

    // ---- A lesson from start to finish -------------------------------------------------------------------

    [Fact]
    public async Task A_learner_takes_a_lesson_gets_feedback_and_finishes_with_score_xp_and_progress()
    {
        using var admin = api.Admin();
        using var learner = api.LearnerAs(Guid.NewGuid());
        var lesson = await Seed.PublishedLessonAsync(admin);

        var started = await learner.PostAsJsonAsync($"{Learning}/attempts", new { lessonId = lesson.LessonId });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var attempt = await started.Content.ReadFromJsonAsync<JsonElement>();
        var attemptId = attempt.GetProperty("attemptId").GetGuid();
        Assert.Equal(2, attempt.GetProperty("totalQuestions").GetInt32());
        Assert.Equal($"{Learning}/attempts/{attemptId}", started.Headers.Location!.OriginalString);

        // Wrong choice: marked wrong, and only now is the right answer shown.
        var wrong = await GetOkAsync(await learner.PostAsJsonAsync($"{Learning}/attempts/{attemptId}/responses?baseLanguage=vi",
            new { questionId = lesson.ChoiceQuestionId, selectedOptionId = lesson.WrongOptionId, timeSpentMs = 2000 }));
        Assert.False(wrong.GetProperty("isCorrect").GetBoolean());
        Assert.Equal(lesson.CorrectOptionId, wrong.GetProperty("correctOptionId").GetGuid());
        Assert.Equal("Hello", wrong.GetProperty("correctAnswer").GetString());
        Assert.Equal(1, wrong.GetProperty("answeredCount").GetInt32());

        // Typed answer: sloppy case, spacing and punctuation still count.
        var right = await GetOkAsync(await learner.PostAsJsonAsync($"{Learning}/attempts/{attemptId}/responses",
            new { questionId = lesson.TypedQuestionId, userInputText = "  GOOD   morning! ", timeSpentMs = 3500 }));
        Assert.True(right.GetProperty("isCorrect").GetBoolean());
        Assert.Equal(JsonValueKind.Null, right.GetProperty("correctAnswer").ValueKind);

        var done = await GetOkAsync(await learner.PutAsync($"{Learning}/attempts/{attemptId}/complete", null));
        Assert.Equal(50, done.GetProperty("scorePercentage").GetInt32());
        Assert.Equal(1, done.GetProperty("correctCount").GetInt32());
        Assert.Equal(2, done.GetProperty("totalQuestions").GetInt32());
        Assert.Equal(10, done.GetProperty("xpEarned").GetInt32());
        Assert.True(done.GetProperty("isFirstCompletion").GetBoolean());
        Assert.Equal(1, done.GetProperty("progress").GetProperty("completedLessonsCount").GetInt32());

        var progress = await GetJsonAsync(learner, $"{Learning}/progress/{lesson.CourseId}");
        Assert.Equal(1, progress.GetProperty("completedLessonsCount").GetInt32());
        Assert.Equal(1, progress.GetProperty("totalLessonsCount").GetInt32());
        Assert.True(progress.GetProperty("isCompleted").GetBoolean());
    }

    [Fact]
    public async Task Course_progress_counts_each_lesson_once_even_when_replayed()
    {
        using var admin = api.Admin();
        using var learner = api.LearnerAs(Guid.NewGuid());
        var courseId = await Seed.CourseAsync(admin);
        var first = await Seed.LessonAsync(admin, courseId, publish: true);
        var second = await Seed.LessonAsync(admin, courseId, publish: true);
        await Seed.PublishCourseAsync(admin, courseId);

        var fresh = await GetJsonAsync(learner, $"{Learning}/progress/{courseId}");
        Assert.Equal((0, 2, false), ReadProgress(fresh));

        await Play(learner, first, allCorrect: true);
        var replay = await Play(learner, first, allCorrect: true);
        Assert.Equal(1, replay.GetProperty("progress").GetProperty("completedLessonsCount").GetInt32());
        Assert.False(replay.GetProperty("isFirstCompletion").GetBoolean());
        Assert.Equal(0, replay.GetProperty("xpEarned").GetInt32());

        Assert.Equal((1, 2, false), ReadProgress(await GetJsonAsync(learner, $"{Learning}/progress/{courseId}")));
        await Play(learner, second, allCorrect: false);
        Assert.Equal((2, 2, true), ReadProgress(await GetJsonAsync(learner, $"{Learning}/progress/{courseId}")));
    }

    [Fact]
    public async Task Progress_belongs_to_the_learner_and_only_published_courses_have_any()
    {
        using var admin = api.Admin();
        using var alice = api.LearnerAs(Guid.NewGuid());
        using var bob = api.LearnerAs(Guid.NewGuid());
        var lesson = await Seed.PublishedLessonAsync(admin);
        var draftCourse = await Seed.CourseAsync(admin);

        await Play(alice, lesson, allCorrect: true);

        Assert.Equal((1, 1, true), ReadProgress(await GetJsonAsync(alice, $"{Learning}/progress/{lesson.CourseId}")));
        Assert.Equal((0, 1, false), ReadProgress(await GetJsonAsync(bob, $"{Learning}/progress/{lesson.CourseId}")));
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"{Learning}/progress/{draftCourse}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"{Learning}/progress/{Guid.NewGuid()}")).StatusCode);
    }

    // ---- Attempt rules --------------------------------------------------------------------------------------

    [Fact]
    public async Task Answers_are_validated_and_one_answer_per_question_is_enforced()
    {
        using var admin = api.Admin();
        using var learner = api.LearnerAs(Guid.NewGuid());
        var lesson = await Seed.PublishedLessonAsync(admin);
        var attemptId = await StartAsync(learner, lesson);
        var url = $"{Learning}/attempts/{attemptId}/responses";

        var textForChoice = await learner.PostAsJsonAsync(url, new { questionId = lesson.ChoiceQuestionId, userInputText = "Hello", timeSpentMs = 1 });
        var foreignOption = await learner.PostAsJsonAsync(url, new { questionId = lesson.ChoiceQuestionId, selectedOptionId = Guid.NewGuid(), timeSpentMs = 1 });
        var optionForTyped = await learner.PostAsJsonAsync(url, new { questionId = lesson.TypedQuestionId, selectedOptionId = lesson.CorrectOptionId, timeSpentMs = 1 });
        var nothing = await learner.PostAsJsonAsync(url, new { questionId = lesson.ChoiceQuestionId, timeSpentMs = 1 });
        var negativeTime = await learner.PostAsJsonAsync(url, new { questionId = lesson.ChoiceQuestionId, selectedOptionId = lesson.CorrectOptionId, timeSpentMs = -5 });
        var unknownQuestion = await learner.PostAsJsonAsync(url, new { questionId = Guid.NewGuid(), selectedOptionId = lesson.CorrectOptionId, timeSpentMs = 1 });

        Assert.All([textForChoice, foreignOption, optionForTyped, nothing, negativeTime], r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(HttpStatusCode.NotFound, unknownQuestion.StatusCode);

        var first = await learner.PostAsJsonAsync(url, new { questionId = lesson.ChoiceQuestionId, selectedOptionId = lesson.CorrectOptionId, timeSpentMs = 1 });
        var second = await learner.PostAsJsonAsync(url, new { questionId = lesson.ChoiceQuestionId, selectedOptionId = lesson.CorrectOptionId, timeSpentMs = 1 });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("response.already_answered", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task One_learner_cannot_touch_anothers_attempt()
    {
        using var admin = api.Admin();
        using var alice = api.LearnerAs(Guid.NewGuid());
        using var mallory = api.LearnerAs(Guid.NewGuid());
        var lesson = await Seed.PublishedLessonAsync(admin);
        var attemptId = await StartAsync(alice, lesson);

        var answer = await mallory.PostAsJsonAsync($"{Learning}/attempts/{attemptId}/responses",
            new { questionId = lesson.ChoiceQuestionId, selectedOptionId = lesson.CorrectOptionId, timeSpentMs = 1 });
        var complete = await mallory.PutAsync($"{Learning}/attempts/{attemptId}/complete", null);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
        // Alice's attempt is untouched: she can still answer both questions and finish.
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"{Learning}/attempts/{attemptId}/responses",
            new { questionId = lesson.ChoiceQuestionId, selectedOptionId = lesson.CorrectOptionId, timeSpentMs = 1 })).StatusCode);
    }

    [Fact]
    public async Task A_lesson_cannot_be_finished_early_or_twice()
    {
        using var admin = api.Admin();
        using var learner = api.LearnerAs(Guid.NewGuid());
        var lesson = await Seed.PublishedLessonAsync(admin);
        var attemptId = await StartAsync(learner, lesson);

        var early = await learner.PutAsync($"{Learning}/attempts/{attemptId}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("attempt.unanswered_questions", (await early.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await AnswerAllAsync(learner, attemptId, lesson, allCorrect: true);
        Assert.Equal(HttpStatusCode.OK, (await learner.PutAsync($"{Learning}/attempts/{attemptId}/complete", null)).StatusCode);

        var twice = await learner.PutAsync($"{Learning}/attempts/{attemptId}/complete", null);
        var lateAnswer = await learner.PostAsJsonAsync($"{Learning}/attempts/{attemptId}/responses",
            new { questionId = lesson.ChoiceQuestionId, selectedOptionId = lesson.CorrectOptionId, timeSpentMs = 1 });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lateAnswer.StatusCode);
    }

    [Fact]
    public async Task Finishing_the_same_attempt_twice_at_once_counts_the_lesson_once()
    {
        // A double click: two completions race. Progress is recomputed from facts, so the lesson is counted once
        // whichever way the race ends.
        using var admin = api.Admin();
        using var learner = api.LearnerAs(Guid.NewGuid());
        var lesson = await Seed.PublishedLessonAsync(admin);
        var attemptId = await StartAsync(learner, lesson);
        await AnswerAllAsync(learner, attemptId, lesson, allCorrect: true);

        var responses = await Task.WhenAll(
            learner.PutAsync($"{Learning}/attempts/{attemptId}/complete", null),
            learner.PutAsync($"{Learning}/attempts/{attemptId}/complete", null));

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"unexpected {(int)r.StatusCode}"));
        Assert.Equal((1, 1, true), ReadProgress(await GetJsonAsync(learner, $"{Learning}/progress/{lesson.CourseId}")));
    }

    [Fact]
    public async Task A_lesson_withdrawn_mid_attempt_stops_the_attempt()
    {
        using var admin = api.Admin();
        using var learner = api.LearnerAs(Guid.NewGuid());
        var lesson = await Seed.PublishedLessonAsync(admin);
        var attemptId = await StartAsync(learner, lesson);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"{ContentApi}/courses/{lesson.CourseId}/archive", null)).StatusCode);

        var answer = await learner.PostAsJsonAsync($"{Learning}/attempts/{attemptId}/responses",
            new { questionId = lesson.ChoiceQuestionId, selectedOptionId = lesson.CorrectOptionId, timeSpentMs = 1 });
        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    // ---- Spaced repetition ----------------------------------------------------------------------------------

    [Fact]
    public async Task Answering_creates_cards_that_are_scheduled_ahead_and_not_due_yet()
    {
        using var admin = api.Admin();
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var lesson = await Seed.PublishedLessonAsync(admin);

        await Play(learner, lesson, allCorrect: true);

        var cards = await CardsOfAsync(userId);
        Assert.Equal(2, cards.Count);
        Assert.All(cards, c =>
        {
            Assert.Equal(1, c.IntervalDays);
            Assert.Equal(1, c.Repetitions);
            Assert.Equal(Today.AddDays(1), c.NextReviewDate);
            Assert.Equal(SrsState.Learning, c.State);
        });
        Assert.Equal(new[] { lesson.ChoiceQuestionId, lesson.TypedQuestionId }.Order(), cards.Select(c => c.VocabularyId).Order());
        Assert.Empty((await GetJsonAsync(learner, $"{Learning}/srs/due")).EnumerateArray());
    }

    [Fact]
    public async Task Redoing_a_lesson_the_same_day_does_not_stretch_the_schedule()
    {
        using var admin = api.Admin();
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var lesson = await Seed.PublishedLessonAsync(admin);

        for (var i = 0; i < 4; i++)
            await Play(learner, lesson, allCorrect: true);

        var cards = await CardsOfAsync(userId);
        Assert.All(cards, c => Assert.Equal(1, c.IntervalDays));
        Assert.Equal(2, await LogsOfAsync(userId));
    }

    [Fact]
    public async Task Only_cards_due_today_or_earlier_are_listed_oldest_first()
    {
        using var admin = api.Admin();
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var lesson = await Seed.PublishedLessonAsync(admin);
        var third = await Seed.LessonAsync(admin, lesson.CourseId, publish: true);
        var overdue = await InsertCardAsync(userId, lesson.ChoiceQuestionId, Today.AddDays(-3));
        var dueToday = await InsertCardAsync(userId, lesson.TypedQuestionId, Today);
        await InsertCardAsync(userId, third.ChoiceQuestionId, Today.AddDays(1));
        await InsertCardAsync(Guid.NewGuid(), third.TypedQuestionId, Today.AddDays(-9));

        var due = (await GetJsonAsync(learner, $"{Learning}/srs/due?baseLanguage=vi")).EnumerateArray().ToList();

        Assert.Equal([overdue, dueToday], due.Select(c => c.GetProperty("cardId").GetGuid()));
        Assert.Equal(["你好", "Good morning"], due.Select(c => c.GetProperty("targetText").GetString()));
        Assert.Equal(Today.AddDays(-3).ToString("yyyy-MM-dd"), due[0].GetProperty("nextReviewDate").GetString());
        Assert.Equal("Chào buổi sáng", due[1].GetProperty("promptTranslation").GetString());
    }

    [Fact]
    public async Task The_due_list_can_be_limited_and_filtered_by_target_language_and_rejects_bad_parameters()
    {
        using var admin = api.Admin();
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var english = await Seed.PublishedLessonAsync(admin, target: "en", level: "A1");
        var chinese = await Seed.PublishedLessonAsync(admin, target: "zh", level: "HSK1");
        await InsertCardAsync(userId, english.ChoiceQuestionId, Today.AddDays(-2), TargetLanguage.English);
        await InsertCardAsync(userId, english.TypedQuestionId, Today.AddDays(-1), TargetLanguage.English);
        await InsertCardAsync(userId, chinese.ChoiceQuestionId, Today, TargetLanguage.Chinese);

        Assert.Equal(3, (await GetJsonAsync(learner, $"{Learning}/srs/due")).GetArrayLength());
        Assert.Equal(1, (await GetJsonAsync(learner, $"{Learning}/srs/due?targetLanguage=zh")).GetArrayLength());
        Assert.Equal(2, (await GetJsonAsync(learner, $"{Learning}/srs/due?targetLanguage=en")).GetArrayLength());
        Assert.Equal(2, (await GetJsonAsync(learner, $"{Learning}/srs/due?limit=2")).GetArrayLength());

        foreach (var bad in new[] { "limit=0", "limit=101", "targetLanguage=fr", "baseLanguage=zz" })
            Assert.Equal(HttpStatusCode.BadRequest, (await learner.GetAsync($"{Learning}/srs/due?{bad}")).StatusCode);
    }

    [Fact]
    public async Task A_card_for_content_that_is_no_longer_published_is_not_offered()
    {
        using var admin = api.Admin();
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var lesson = await Seed.PublishedLessonAsync(admin);
        await InsertCardAsync(userId, lesson.ChoiceQuestionId, Today.AddDays(-1));
        Assert.Equal(1, (await GetJsonAsync(learner, $"{Learning}/srs/due")).GetArrayLength());

        await admin.PutAsync($"{ContentApi}/courses/{lesson.CourseId}/archive", null);

        Assert.Equal(0, (await GetJsonAsync(learner, $"{Learning}/srs/due")).GetArrayLength());
    }

    [Fact]
    public async Task Rating_a_due_card_schedules_it_logs_it_and_takes_it_off_todays_list()
    {
        using var admin = api.Admin();
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var lesson = await Seed.PublishedLessonAsync(admin);
        var cardId = await InsertCardAsync(userId, lesson.ChoiceQuestionId, Today);

        var response = await learner.PostAsJsonAsync($"{Learning}/srs/review", new { cardId, rating = 3 });

        var result = await GetOkAsync(response);
        Assert.Equal(1, result.GetProperty("intervalDays").GetInt32());
        Assert.Equal(1, result.GetProperty("repetitions").GetInt32());
        Assert.Equal(2.5, result.GetProperty("easeFactor").GetDouble());
        Assert.Equal(Today.AddDays(1).ToString("yyyy-MM-dd"), result.GetProperty("nextReviewDate").GetString());
        Assert.Equal("Learning", result.GetProperty("state").GetString());

        var card = Assert.Single(await CardsOfAsync(userId));
        Assert.Equal((1, 1, Today.AddDays(1)), (card.IntervalDays, card.Repetitions, card.NextReviewDate));
        Assert.NotNull(card.LastReviewDate);

        using var scope = Scope(out var db);
        var log = await db.ReviewLogs.SingleAsync(l => l.UserVocabularyId == cardId);
        Assert.Equal((ReviewRating.Good, 2.5, 2.5, 0, 1), (log.Rating, log.OldEaseFactor, log.NewEaseFactor, log.OldIntervalDays, log.NewIntervalDays));

        Assert.Empty((await GetJsonAsync(learner, $"{Learning}/srs/due")).EnumerateArray());
    }

    [Theory]
    [InlineData(1, 1, 2.3, 0)]
    [InlineData(2, 1, 2.35, 0)]
    [InlineData(3, 1, 2.5, 1)]
    [InlineData(4, 2, 2.65, 1)]
    public async Task Each_rating_applies_its_own_sm2_step(int rating, int interval, double ease, int repetitions)
    {
        using var admin = api.Admin();
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var lesson = await Seed.PublishedLessonAsync(admin);
        var cardId = await InsertCardAsync(userId, lesson.ChoiceQuestionId, Today);

        var result = await GetOkAsync(await learner.PostAsJsonAsync($"{Learning}/srs/review", new { cardId, rating }));

        Assert.Equal((interval, ease, repetitions), (result.GetProperty("intervalDays").GetInt32(), result.GetProperty("easeFactor").GetDouble(), result.GetProperty("repetitions").GetInt32()));
        Assert.Equal(Today.AddDays(interval).ToString("yyyy-MM-dd"), result.GetProperty("nextReviewDate").GetString());
    }

    [Fact]
    public async Task An_overdue_established_card_is_scheduled_from_its_interval_and_today()
    {
        using var admin = api.Admin();
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var lesson = await Seed.PublishedLessonAsync(admin);
        var cardId = await InsertCardAsync(userId, lesson.ChoiceQuestionId, Today.AddDays(-10), interval: 6, repetitions: 2);

        var result = await GetOkAsync(await learner.PostAsJsonAsync($"{Learning}/srs/review", new { cardId, rating = 3 }));

        Assert.Equal(15, result.GetProperty("intervalDays").GetInt32());
        Assert.Equal(Today.AddDays(15).ToString("yyyy-MM-dd"), result.GetProperty("nextReviewDate").GetString());
        Assert.Equal("Review", result.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_card_cannot_be_reviewed_early_or_twice_in_a_day_or_by_someone_else()
    {
        using var admin = api.Admin();
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        using var stranger = api.LearnerAs(Guid.NewGuid());
        var lesson = await Seed.PublishedLessonAsync(admin);
        var future = await InsertCardAsync(userId, lesson.ChoiceQuestionId, Today.AddDays(4));
        var due = await InsertCardAsync(userId, lesson.TypedQuestionId, Today);

        var early = await learner.PostAsJsonAsync($"{Learning}/srs/review", new { cardId = future, rating = 4 });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("srs.card_not_due", (await early.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"{Learning}/srs/review", new { cardId = due, rating = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await learner.PostAsJsonAsync($"{Learning}/srs/review", new { cardId = Guid.NewGuid(), rating = 3 })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await learner.PostAsJsonAsync($"{Learning}/srs/review", new { cardId = due, rating = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await learner.PostAsJsonAsync($"{Learning}/srs/review", new { cardId = due, rating = 3 })).StatusCode);

        using var scope = Scope(out var db);
        Assert.Equal(1, await db.ReviewLogs.CountAsync(l => l.UserVocabularyId == due || l.UserVocabularyId == future));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public async Task Ratings_outside_one_to_four_are_rejected(int rating)
    {
        using var admin = api.Admin();
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var lesson = await Seed.PublishedLessonAsync(admin);
        var cardId = await InsertCardAsync(userId, lesson.ChoiceQuestionId, Today);

        var response = await learner.PostAsJsonAsync($"{Learning}/srs/review", new { cardId, rating });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await LogsOfAsync(userId));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private IServiceScope Scope(out LinguaBridgeDbContext db)
    {
        var scope = api.Services.CreateScope();
        db = scope.ServiceProvider.GetRequiredService<LinguaBridgeDbContext>();
        return scope;
    }

    private async Task<Guid> InsertCardAsync(Guid userId, Guid questionId, DateOnly nextReview, TargetLanguage language = TargetLanguage.English, int interval = 0, int repetitions = 0)
    {
        var card = new UserVocabularyProgress(userId, questionId, language, nextReview);
        if (interval > 0 || repetitions > 0)
            card.ApplyReview(new Sm2Result(2.5, interval, repetitions, nextReview, SrsState.Review), DateTimeOffset.UtcNow.AddDays(-interval));

        using var scope = Scope(out var db);
        db.UserVocabularyProgress.Add(card);
        await db.SaveChangesAsync();
        return card.Id;
    }

    private async Task<List<UserVocabularyProgress>> CardsOfAsync(Guid userId)
    {
        using var scope = Scope(out var db);
        return await db.UserVocabularyProgress.AsNoTracking().Where(c => c.UserId == userId).ToListAsync();
    }

    private async Task<int> LogsOfAsync(Guid userId)
    {
        using var scope = Scope(out var db);
        return await db.ReviewLogs.CountAsync(l => db.UserVocabularyProgress.Any(c => c.Id == l.UserVocabularyId && c.UserId == userId));
    }

    private static async Task<Guid> StartAsync(HttpClient learner, SeededLesson lesson)
    {
        var response = await learner.PostAsJsonAsync($"{Learning}/attempts", new { lessonId = lesson.LessonId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attemptId").GetGuid();
    }

    private static async Task AnswerAllAsync(HttpClient learner, Guid attemptId, SeededLesson lesson, bool allCorrect)
    {
        var choice = await learner.PostAsJsonAsync($"{Learning}/attempts/{attemptId}/responses",
            new { questionId = lesson.ChoiceQuestionId, selectedOptionId = allCorrect ? lesson.CorrectOptionId : lesson.WrongOptionId, timeSpentMs = 800 });
        var typed = await learner.PostAsJsonAsync($"{Learning}/attempts/{attemptId}/responses",
            new { questionId = lesson.TypedQuestionId, userInputText = allCorrect ? lesson.TypedAnswer : "wrong", timeSpentMs = 800 });
        Assert.Equal(HttpStatusCode.OK, choice.StatusCode);
        Assert.Equal(HttpStatusCode.OK, typed.StatusCode);
    }

    private static async Task<JsonElement> Play(HttpClient learner, SeededLesson lesson, bool allCorrect)
    {
        var attemptId = await StartAsync(learner, lesson);
        await AnswerAllAsync(learner, attemptId, lesson, allCorrect);
        return await GetOkAsync(await learner.PutAsync($"{Learning}/attempts/{attemptId}/complete", null));
    }

    private static (int Completed, int Total, bool IsCompleted) ReadProgress(JsonElement progress) =>
        (progress.GetProperty("completedLessonsCount").GetInt32(), progress.GetProperty("totalLessonsCount").GetInt32(), progress.GetProperty("isCompleted").GetBoolean());

    private static async Task<JsonElement> GetOkAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path) => await GetOkAsync(await client.GetAsync(path));
}

internal sealed record SeededLesson(
    Guid CourseId,
    Guid LessonId,
    Guid ChoiceQuestionId,
    Guid CorrectOptionId,
    Guid WrongOptionId,
    Guid TypedQuestionId,
    string TypedAnswer);

/// <summary>Authors content through the admin API, the way an editor would.</summary>
internal static class Seed
{
    private const string Content = "/api/content";

    public static async Task<Guid> CourseAsync(HttpClient admin, string target = "en", string level = "A1")
    {
        var response = await admin.PostAsJsonAsync($"{Content}/courses", new { code = $"LRN_{Guid.NewGuid():N}", targetLanguage = target, level });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await IdAsync(response);
    }

    /// <summary>A lesson with a choose-one question (你好 / Hello) and a typed one (type "Good morning"), optionally published.</summary>
    public static async Task<SeededLesson> LessonAsync(HttpClient admin, Guid courseId, bool publish)
    {
        var lessonResponse = await admin.PostAsJsonAsync($"{Content}/courses/{courseId}/lessons", new { lessonType = "Standard", xpReward = 20 });
        Assert.Equal(HttpStatusCode.Created, lessonResponse.StatusCode);
        var lessonId = await IdAsync(lessonResponse);

        var choiceResponse = await admin.PostAsJsonAsync($"{Content}/lessons/{lessonId}/questions", new
        {
            questionType = "MultipleChoice",
            targetText = "你好",
            pinyinOrIpa = "nǐ hǎo",
            translations = new[] { new { baseLanguage = "vi", promptTranslation = "Chào hỏi nghĩa là gì?", explanation = "Đây là lời chào." } },
        });
        Assert.Equal(HttpStatusCode.Created, choiceResponse.StatusCode);
        var choiceId = await IdAsync(choiceResponse);

        await AddOptionAsync(admin, choiceId, "Goodbye", false);
        var correct = await AddOptionAsync(admin, choiceId, "Hello", true);
        var wrong = await AddOptionAsync(admin, choiceId, "Thanks", false);

        var typedResponse = await admin.PostAsJsonAsync($"{Content}/lessons/{lessonId}/questions", new
        {
            questionType = "Translate",
            targetText = "Good morning",
            translations = new[] { new { baseLanguage = "vi", promptTranslation = "Chào buổi sáng", explanation = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.Created, typedResponse.StatusCode);
        var typedId = await IdAsync(typedResponse);

        if (publish)
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{Content}/lessons/{lessonId}/publish", null)).StatusCode);

        return new SeededLesson(courseId, lessonId, choiceId, correct, wrong, typedId, "Good morning");
    }

    public static async Task PublishCourseAsync(HttpClient admin, Guid courseId) =>
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"{Content}/courses/{courseId}/publish", null)).StatusCode);

    /// <summary>A published course holding one published lesson.</summary>
    public static async Task<SeededLesson> PublishedLessonAsync(HttpClient admin, string target = "en", string level = "A1")
    {
        var courseId = await CourseAsync(admin, target, level);
        var lesson = await LessonAsync(admin, courseId, publish: true);
        await PublishCourseAsync(admin, courseId);
        return lesson;
    }

    private static async Task<Guid> AddOptionAsync(HttpClient admin, Guid questionId, string text, bool isCorrect)
    {
        var response = await admin.PostAsJsonAsync($"{Content}/questions/{questionId}/options", new { optionText = text, isCorrect });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await IdAsync(response);
    }

    private static async Task<Guid> IdAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
}
