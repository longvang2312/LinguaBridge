using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LinguaBridge.IntegrationTests;

/// <summary>
/// Drives the Content API end to end (HTTP, authorization, handlers, EF Core, PostgreSQL). All tests share one
/// database, so each creates its own courses under unique codes and asserts only about those.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ContentApiTests(ContentApiFixture api) : IClassFixture<ContentApiFixture>
{
    private const string Api = "/api/content";

    // ---- Authorization ------------------------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_callers_are_rejected_everywhere()
    {
        using var client = api.Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{Api}/courses?targetLanguage=en")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"{Api}/courses", NewCourseBody())).StatusCode);
    }

    [Fact]
    public async Task Learners_cannot_use_any_admin_endpoint()
    {
        using var learner = api.Learner();
        var id = Guid.NewGuid();

        var attempts = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/courses/admin"),
            (HttpMethod.Post, "/courses"),
            (HttpMethod.Put, $"/courses/{id}"),
            (HttpMethod.Put, $"/courses/{id}/publish"),
            (HttpMethod.Put, $"/courses/{id}/archive"),
            (HttpMethod.Post, $"/courses/{id}/translations"),
            (HttpMethod.Put, $"/courses/{id}/translations/vi"),
            (HttpMethod.Post, $"/courses/{id}/lessons"),
            (HttpMethod.Post, $"/lessons/{id}/publish"),
            (HttpMethod.Post, $"/lessons/{id}/translations"),
            (HttpMethod.Get, $"/lessons/{id}/preview"),
            (HttpMethod.Post, $"/lessons/{id}/questions"),
            (HttpMethod.Post, $"/questions/{id}/options"),
        };

        foreach (var (method, path) in attempts)
        {
            using var request = new HttpRequestMessage(method, Api + path) { Content = JsonContent.Create(new { }) };
            var response = await learner.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} returned {(int)response.StatusCode}, expected 403.");
        }
    }

    [Theory]
    [InlineData("Vip")]
    [InlineData("ContentAdmin")]
    [InlineData("globaladmin")]
    public async Task Other_roles_do_not_grant_admin_rights(string role)
    {
        // Vip is a learner perk owned by Billing; only the exact GlobalAdmin role administers content.
        using var client = api.Learner(role);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Api}/courses/admin")).StatusCode);
    }

    // ---- Creating courses ---------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_creates_a_draft_course_and_gets_a_location()
    {
        using var admin = api.Admin();
        var code = UniqueCode();

        var response = await admin.PostAsJsonAsync($"{Api}/courses", NewCourseBody(code, "zh", "HSK2"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal($"{Api}/courses/{id}", response.Headers.Location!.OriginalString);

        var detail = await GetJsonAsync(admin, $"{Api}/courses/{id}");
        var course = detail.GetProperty("course");
        Assert.Equal(code, course.GetProperty("code").GetString());
        Assert.Equal("zh", course.GetProperty("targetLanguage").GetString());
        Assert.Equal("HSK2", course.GetProperty("level").GetString());
        Assert.Equal("Draft", course.GetProperty("status").GetString());
        Assert.Equal(1, course.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Invalid_course_input_returns_400_problem_details_with_field_errors()
    {
        using var admin = api.Admin();

        var response = await admin.PostAsJsonAsync($"{Api}/courses", new { code = "", targetLanguage = "fr", level = "A1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Code", out _));
        Assert.True(errors.TryGetProperty("TargetLanguage", out _));
    }

    [Fact]
    public async Task A_level_from_the_other_language_is_rejected()
    {
        using var admin = api.Admin();

        var response = await admin.PostAsJsonAsync($"{Api}/courses", NewCourseBody(UniqueCode(), "en", "HSK1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_course_code_returns_409()
    {
        using var admin = api.Admin();
        var code = UniqueCode();
        await CreateCourseAsync(admin, code);

        var response = await admin.PostAsJsonAsync($"{Api}/courses", NewCourseBody(code));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("course.code_taken", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Concurrent_creates_of_one_code_produce_exactly_one_course()
    {
        // The up-front check cannot stop a race; the unique index does, and it must surface as 409, not 500.
        using var admin = api.Admin();
        var code = UniqueCode();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => admin.PostAsJsonAsync($"{Api}/courses", NewCourseBody(code))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var all = await GetJsonAsync(admin, $"{Api}/courses/admin?targetLanguage=en");
        Assert.Equal(1, all.EnumerateArray().Count(c => c.GetProperty("code").GetString() == code));
    }

    // ---- Lifecycle ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_course_cannot_be_published_until_it_has_a_published_lesson()
    {
        using var admin = api.Admin();
        var courseId = await CreateCourseAsync(admin);

        var empty = await admin.PutAsync($"{Api}/courses/{courseId}/publish", null);
        Assert.Equal(HttpStatusCode.Conflict, empty.StatusCode);
        Assert.Equal("course.no_published_lesson", (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var lessonId = await CreateLessonAsync(admin, courseId);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync($"{Api}/courses/{courseId}/publish", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{Api}/lessons/{lessonId}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"{Api}/courses/{courseId}/publish", null)).StatusCode);
        Assert.Equal("Published", (await GetJsonAsync(admin, $"{Api}/courses/{courseId}")).GetProperty("course").GetProperty("status").GetString());

        // Already published: not a silent no-op.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync($"{Api}/courses/{courseId}/publish", null)).StatusCode);
    }

    [Fact]
    public async Task Unknown_ids_return_404_problem_details()
    {
        using var admin = api.Admin();
        var missing = Guid.NewGuid();

        foreach (var response in new[]
        {
            await admin.PutAsync($"{Api}/courses/{missing}/publish", null),
            await admin.PutAsync($"{Api}/courses/{missing}/archive", null),
            await admin.PostAsync($"{Api}/lessons/{missing}/publish", null),
            await admin.GetAsync($"{Api}/lessons/{missing}/preview"),
            await admin.GetAsync($"{Api}/courses/{missing}"),
            await admin.GetAsync($"{Api}/courses/{missing}/lessons"),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task Admin_updates_level_and_thumbnail_and_bad_levels_are_rejected()
    {
        using var admin = api.Admin();
        var courseId = await CreateCourseAsync(admin, level: "A1");

        var ok = await admin.PutAsJsonAsync($"{Api}/courses/{courseId}", new { level = "B2", thumbnailUrl = "https://cdn.example/b2.png" });
        var wrongLanguage = await admin.PutAsJsonAsync($"{Api}/courses/{courseId}", new { level = "HSK1", thumbnailUrl = (string?)null });
        var missing = await admin.PutAsJsonAsync($"{Api}/courses/{Guid.NewGuid()}", new { level = "A1", thumbnailUrl = (string?)null });

        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongLanguage.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var course = (await GetJsonAsync(admin, $"{Api}/courses/{courseId}")).GetProperty("course");
        Assert.Equal("B2", course.GetProperty("level").GetString());
        Assert.Equal("https://cdn.example/b2.png", course.GetProperty("thumbnailUrl").GetString());
    }

    [Fact]
    public async Task Archiving_removes_a_course_from_learners_and_freezes_it()
    {
        using var admin = api.Admin();
        using var learner = api.Learner();
        var courseId = await CreatePublishedCourseAsync(admin);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"{Api}/courses/{courseId}/archive", null)).StatusCode);

        Assert.DoesNotContain(courseId, await PublishedCourseIdsAsync(learner, "en"));
        Assert.Equal(HttpStatusCode.NotFound, (await learner.GetAsync($"{Api}/courses/{courseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await learner.GetAsync($"{Api}/courses/{courseId}/lessons")).StatusCode);
        // Admins still see it, and cannot change it any more.
        Assert.Equal("Archived", (await GetJsonAsync(admin, $"{Api}/courses/{courseId}")).GetProperty("course").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{Api}/courses/{courseId}/lessons", new { lessonType = "Standard" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync($"{Api}/courses/{courseId}/archive", null)).StatusCode);
    }

    // ---- Visibility ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Learners_see_published_courses_only_and_admins_see_everything()
    {
        using var admin = api.Admin();
        using var learner = api.Learner();
        var draft = await CreateCourseAsync(admin);
        var published = await CreatePublishedCourseAsync(admin);
        var chinese = await CreatePublishedCourseAsync(admin, target: "zh", level: "HSK1");

        var learnerEnglish = await PublishedCourseIdsAsync(learner, "en");
        var learnerChinese = await PublishedCourseIdsAsync(learner, "zh");

        Assert.Contains(published, learnerEnglish);
        Assert.DoesNotContain(draft, learnerEnglish);
        Assert.DoesNotContain(chinese, learnerEnglish);
        Assert.Contains(chinese, learnerChinese);
        Assert.DoesNotContain(published, learnerChinese);

        var adminAll = (await GetJsonAsync(admin, $"{Api}/courses/admin")).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(draft, adminAll);
        Assert.Contains(published, adminAll);
        Assert.Contains(chinese, adminAll);

        var adminEnglish = (await GetJsonAsync(admin, $"{Api}/courses/admin?targetLanguage=en")).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(draft, adminEnglish);
        Assert.DoesNotContain(chinese, adminEnglish);
    }

    [Fact]
    public async Task A_draft_course_is_invisible_to_learners_and_visible_to_admins()
    {
        using var admin = api.Admin();
        using var learner = api.Learner();
        var courseId = await CreateCourseAsync(admin);

        // 404, not 403: a learner must not be able to tell a draft exists.
        Assert.Equal(HttpStatusCode.NotFound, (await learner.GetAsync($"{Api}/courses/{courseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await learner.GetAsync($"{Api}/courses/{courseId}/lessons")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Api}/courses/{courseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Api}/courses/{courseId}/lessons")).StatusCode);
    }

    [Fact]
    public async Task Lessons_are_filtered_by_status_for_learners_and_ordered_for_everyone()
    {
        using var admin = api.Admin();
        using var learner = api.Learner();
        var courseId = await CreateCourseAsync(admin);
        var second = await CreateLessonAsync(admin, courseId, orderIndex: 5);
        var first = await CreateLessonAsync(admin, courseId, orderIndex: 1);
        var draft = await CreateLessonAsync(admin, courseId, orderIndex: 9);
        await admin.PostAsync($"{Api}/lessons/{first}/publish", null);
        await admin.PostAsync($"{Api}/lessons/{second}/publish", null);
        await admin.PutAsync($"{Api}/courses/{courseId}/publish", null);

        var forLearner = await LessonIdsAsync(learner, courseId);
        var forAdmin = await LessonIdsAsync(admin, courseId);

        Assert.Equal([first, second], forLearner);
        Assert.Equal([first, second, draft], forAdmin);
    }

    [Fact]
    public async Task A_learner_cannot_widen_the_view_through_the_request()
    {
        // Visibility comes from the caller's role, not from anything in the query string or headers.
        using var admin = api.Admin();
        using var learner = api.Learner();
        var courseId = await CreateCourseAsync(admin);

        var response = await learner.GetAsync($"{Api}/courses/{courseId}/lessons?includeUnpublished=true");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Lessons_added_after_publishing_stay_hidden_until_published()
    {
        using var admin = api.Admin();
        using var learner = api.Learner();
        var courseId = await CreatePublishedCourseAsync(admin);
        var published = (await LessonIdsAsync(learner, courseId)).Single();

        var late = await CreateLessonAsync(admin, courseId);

        Assert.Equal([published], await LessonIdsAsync(learner, courseId));
        await admin.PostAsync($"{Api}/lessons/{late}/publish", null);
        Assert.Equal([published, late], await LessonIdsAsync(learner, courseId));
    }

    // ---- Preview, questions and options -------------------------------------------------------------------

    [Fact]
    public async Task Admin_previews_a_draft_lesson_with_questions_options_and_answers()
    {
        using var admin = api.Admin();
        var courseId = await CreateCourseAsync(admin);
        var lessonId = await CreateLessonAsync(admin, courseId);

        var questionResponse = await admin.PostAsJsonAsync($"{Api}/lessons/{lessonId}/questions", new
        {
            questionType = "MultipleChoice",
            targetText = "Good morning",
            pinyinOrIpa = "ɡʊd ˈmɔːrnɪŋ",
            translations = new[] { new { baseLanguage = "vi", promptTranslation = "Chào buổi sáng nghĩa là gì?", explanation = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.Created, questionResponse.StatusCode);
        var questionId = (await questionResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var correct = await admin.PostAsJsonAsync($"{Api}/questions/{questionId}/options", new { optionText = "Chào buổi sáng", isCorrect = true });
        var wrong = await admin.PostAsJsonAsync($"{Api}/questions/{questionId}/options", new { optionText = "Chúc ngủ ngon", isCorrect = false });
        Assert.Equal(HttpStatusCode.Created, correct.StatusCode);
        Assert.Equal(HttpStatusCode.Created, wrong.StatusCode);

        var preview = await GetJsonAsync(admin, $"{Api}/lessons/{lessonId}/preview?baseLanguage=vi");

        Assert.Equal("Draft", preview.GetProperty("lesson").GetProperty("status").GetString());
        var question = Assert.Single(preview.GetProperty("questions").EnumerateArray());
        Assert.Equal("Good morning", question.GetProperty("targetText").GetString());
        Assert.Equal("Chào buổi sáng nghĩa là gì?", question.GetProperty("promptTranslation").GetString());
        var options = question.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(["Chào buổi sáng", "Chúc ngủ ngon"], options.Select(o => o.GetProperty("optionText").GetString()));
        Assert.Equal([true, false], options.Select(o => o.GetProperty("isCorrect").GetBoolean()));
        Assert.Equal([0, 1], options.Select(o => o.GetProperty("orderIndex").GetInt32()));
    }

    [Fact]
    public async Task Questions_are_appended_in_order_and_invalid_ones_are_rejected()
    {
        using var admin = api.Admin();
        var lessonId = await CreateLessonAsync(admin, await CreateCourseAsync(admin));

        foreach (var text in new[] { "one", "two", "three" })
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"{Api}/lessons/{lessonId}/questions", new { questionType = "Translate", targetText = text })).StatusCode);

        var badType = await admin.PostAsJsonAsync($"{Api}/lessons/{lessonId}/questions", new { questionType = "Essay", targetText = "x" });
        var noLesson = await admin.PostAsJsonAsync($"{Api}/lessons/{Guid.NewGuid()}/questions", new { questionType = "Translate", targetText = "x" });
        var noQuestion = await admin.PostAsJsonAsync($"{Api}/questions/{Guid.NewGuid()}/options", new { optionText = "x", isCorrect = true });

        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noLesson.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noQuestion.StatusCode);
        var preview = await GetJsonAsync(admin, $"{Api}/lessons/{lessonId}/preview");
        var questions = preview.GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal(["one", "two", "three"], questions.Select(q => q.GetProperty("targetText").GetString()));
        Assert.Equal([0, 1, 2], questions.Select(q => q.GetProperty("orderIndex").GetInt32()));
    }

    // ---- Translations -------------------------------------------------------------------------------------

    [Fact]
    public async Task Course_titles_follow_the_requested_language_with_english_as_fallback()
    {
        using var admin = api.Admin();
        using var learner = api.Learner();
        var courseId = await CreatePublishedCourseAsync(admin);

        // No translation at all yet.
        var none = (await GetJsonAsync(learner, $"{Api}/courses/{courseId}?baseLanguage=vi")).GetProperty("course");
        Assert.Equal(JsonValueKind.Null, none.GetProperty("title").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"{Api}/courses/{courseId}/translations", new { baseLanguage = "en", title = "English for beginners", description = "Start here" })).StatusCode);
        var fallback = (await GetJsonAsync(learner, $"{Api}/courses/{courseId}?baseLanguage=vi")).GetProperty("course");
        Assert.Equal("English for beginners", fallback.GetProperty("title").GetString());
        Assert.Equal("en", fallback.GetProperty("translationLanguage").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"{Api}/courses/{courseId}/translations", new { baseLanguage = "vi", title = "Tiếng Anh cho người mới" })).StatusCode);
        var vietnamese = await GetJsonAsync(learner, $"{Api}/courses/{courseId}?baseLanguage=vi");
        Assert.Equal("Tiếng Anh cho người mới", vietnamese.GetProperty("course").GetProperty("title").GetString());
        Assert.Equal(2, vietnamese.GetProperty("translations").GetArrayLength());

        var listed = (await GetJsonAsync(learner, $"{Api}/courses?baseLanguage=vi&targetLanguage=en")).EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == courseId);
        Assert.Equal("Tiếng Anh cho người mới", listed.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Translations_can_be_added_once_and_then_updated()
    {
        using var admin = api.Admin();
        var courseId = await CreateCourseAsync(admin);
        var body = new { baseLanguage = "vi", title = "Bản đầu" };

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"{Api}/courses/{courseId}/translations", body)).StatusCode);
        var duplicate = await admin.PostAsJsonAsync($"{Api}/courses/{courseId}/translations", body);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("course.translation_exists", (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"{Api}/courses/{courseId}/translations/vi", new { title = "Bản sửa", description = "Mô tả" })).StatusCode);
        var updated = (await GetJsonAsync(admin, $"{Api}/courses/{courseId}?baseLanguage=vi")).GetProperty("course");
        Assert.Equal("Bản sửa", updated.GetProperty("title").GetString());
        Assert.Equal("Mô tả", updated.GetProperty("description").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"{Api}/courses/{courseId}/translations/en", new { title = "Missing" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Api}/courses/{courseId}/translations/xx", new { title = "Bad language" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Api}/courses/{courseId}/translations", new { baseLanguage = "en", title = " " })).StatusCode);
    }

    [Fact]
    public async Task Lesson_translations_are_returned_in_the_lesson_list()
    {
        using var admin = api.Admin();
        using var learner = api.Learner();
        var courseId = await CreateCourseAsync(admin);
        var lessonId = await CreateLessonAsync(admin, courseId);
        await admin.PostAsync($"{Api}/lessons/{lessonId}/publish", null);
        await admin.PutAsync($"{Api}/courses/{courseId}/publish", null);

        var added = await admin.PostAsJsonAsync($"{Api}/lessons/{lessonId}/translations", new { baseLanguage = "vi", title = "Chào hỏi", guideText = "Cách chào" });
        var duplicate = await admin.PostAsJsonAsync($"{Api}/lessons/{lessonId}/translations", new { baseLanguage = "vi", title = "Khác" });

        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var lesson = (await GetJsonAsync(learner, $"{Api}/courses/{courseId}/lessons?baseLanguage=vi")).EnumerateArray().Single();
        Assert.Equal("Chào hỏi", lesson.GetProperty("title").GetString());
        Assert.Equal("Cách chào", lesson.GetProperty("guideText").GetString());
    }

    [Theory]
    [InlineData("/courses?targetLanguage=fr")]
    [InlineData("/courses")]
    [InlineData("/courses?targetLanguage=en&baseLanguage=zz")]
    [InlineData("/courses/admin?targetLanguage=fr")]
    [InlineData("/courses/admin?baseLanguage=zz")]
    public async Task Unsupported_language_parameters_return_400(string path)
    {
        using var admin = api.Admin();

        var response = await admin.GetAsync(Api + path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private static string UniqueCode() => $"IT_{Guid.NewGuid():N}";

    private static object NewCourseBody(string? code = null, string target = "en", string level = "A1") =>
        new { code = code ?? UniqueCode(), targetLanguage = target, level };

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"GET {path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CreateCourseAsync(HttpClient admin, string? code = null, string target = "en", string level = "A1")
    {
        var response = await admin.PostAsJsonAsync($"{Api}/courses", NewCourseBody(code, target, level));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateLessonAsync(HttpClient admin, Guid courseId, int? orderIndex = null)
    {
        var response = await admin.PostAsJsonAsync($"{Api}/courses/{courseId}/lessons", new { lessonType = "Standard", orderIndex });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreatePublishedCourseAsync(HttpClient admin, string target = "en", string level = "A1")
    {
        var courseId = await CreateCourseAsync(admin, target: target, level: level);
        var lessonId = await CreateLessonAsync(admin, courseId);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{Api}/lessons/{lessonId}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"{Api}/courses/{courseId}/publish", null)).StatusCode);
        return courseId;
    }

    private static async Task<List<Guid>> PublishedCourseIdsAsync(HttpClient client, string target) =>
        (await GetJsonAsync(client, $"{Api}/courses?targetLanguage={target}")).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();

    private static async Task<List<Guid>> LessonIdsAsync(HttpClient client, Guid courseId) =>
        (await GetJsonAsync(client, $"{Api}/courses/{courseId}/lessons")).EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();
}
