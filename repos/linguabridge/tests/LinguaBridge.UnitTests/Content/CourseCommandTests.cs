using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content.Commands;
using LinguaBridge.Modules.Content.Entities;

namespace LinguaBridge.UnitTests.Content;

public class CourseCommandTests
{
    private readonly FakeContentRepository _repository = new();

    private CreateCourseHandler Create => new(_repository, new CreateCourseCommandValidator());

    // ---- Create -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_makes_a_draft_course_at_version_one()
    {
        var result = await Create.HandleAsync(new CreateCourseCommand("ZH_HSK_1", "zh", "HSK1", "https://cdn/x.png"));

        Assert.True(result.IsSuccess);
        var course = Assert.Single(_repository.Courses.Values);
        Assert.Equal(result.Value, course.Id);
        Assert.Equal(ContentStatus.Draft, course.Status);
        Assert.Equal(1, course.Version);
        Assert.Equal(TargetLanguage.Chinese, course.TargetLanguage);
        Assert.Equal(LanguageLevel.Hsk1, course.Level);
        Assert.Equal(1, _repository.SaveCount);
    }

    [Fact]
    public async Task Create_accepts_codes_in_any_case_and_trims_the_course_code()
    {
        var result = await Create.HandleAsync(new CreateCourseCommand("  EN_CEFR_B2 ", " EN ", "b2"));

        Assert.True(result.IsSuccess);
        Assert.Equal("EN_CEFR_B2", _repository.Courses.Values.Single().Code);
    }

    [Theory]
    [InlineData("", "en", "A1", "Code")]
    [InlineData("   ", "en", "A1", "Code")]
    [InlineData("fr", "fr", "A1", "TargetLanguage")]
    [InlineData("X", "", "A1", "TargetLanguage")]
    [InlineData("X", "en", "Z9", "Level")]
    [InlineData("X", "en", "1", "Level")]
    [InlineData("X", "en", "HSK1", "Level")]
    [InlineData("X", "zh", "B2", "Level")]
    public async Task Create_rejects_invalid_input_without_saving(string code, string target, string level, string invalidField)
    {
        var result = await Create.HandleAsync(new CreateCourseCommand(code, target, level));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Contains(invalidField, result.Error.Details!.Keys);
        Assert.Empty(_repository.Courses);
        Assert.Equal(0, _repository.SaveCount);
    }

    [Fact]
    public async Task Create_rejects_a_code_longer_than_the_column()
    {
        var result = await Create.HandleAsync(new CreateCourseCommand(new string('X', Course.CodeMaxLength + 1), "en", "A1"));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Contains("Code", result.Error.Details!.Keys);
    }

    [Fact]
    public async Task Create_reports_a_taken_code_as_conflict()
    {
        _repository.AddCourse("EN_CEFR_A1");

        var result = await Create.HandleAsync(new CreateCourseCommand("EN_CEFR_A1", "en", "A1"));

        Assert.Equal("course.code_taken", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Single(_repository.Courses);
    }

    [Fact]
    public async Task Create_reports_a_lost_race_on_the_code_as_the_same_conflict()
    {
        _repository.FailNextSaveWithUniqueViolation = true;

        var result = await Create.HandleAsync(new CreateCourseCommand("EN_CEFR_A1", "en", "A1"));

        Assert.Equal("course.code_taken", result.Error!.Code);
    }

    // ---- Update -------------------------------------------------------------------------------------------

    private UpdateCourseHandler Update => new(_repository, new UpdateCourseCommandValidator());

    [Fact]
    public async Task Update_changes_level_and_thumbnail()
    {
        var course = _repository.AddCourse(level: LanguageLevel.A1);

        var result = await Update.HandleAsync(new UpdateCourseCommand(course.Id, "B1", "https://cdn/new.png"));

        Assert.True(result.IsSuccess);
        Assert.Equal(LanguageLevel.B1, course.Level);
        Assert.Equal("https://cdn/new.png", course.ThumbnailUrl);
        Assert.Equal(1, _repository.SaveCount);
    }

    [Fact]
    public async Task Update_with_a_null_thumbnail_clears_it()
    {
        var course = new Course("EN_X", TargetLanguage.English, LanguageLevel.A1, "https://cdn/old.png");
        _repository.Courses[course.Id] = course;

        await Update.HandleAsync(new UpdateCourseCommand(course.Id, "A1", null));

        Assert.Null(course.ThumbnailUrl);
    }

    [Fact]
    public async Task Update_rejects_a_level_of_the_other_language()
    {
        var course = _repository.AddCourse();

        var result = await Update.HandleAsync(new UpdateCourseCommand(course.Id, "HSK2", null));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Contains("Level", result.Error.Details!.Keys);
        Assert.Equal(LanguageLevel.A1, course.Level);
        Assert.Equal(0, _repository.SaveCount);
    }

    [Fact]
    public async Task Update_rejects_unknown_level_and_missing_or_archived_course()
    {
        var course = _repository.AddCourse();
        var archived = _repository.AddCourse("EN_OLD");
        archived.Archive();

        Assert.Equal(ErrorType.Validation, (await Update.HandleAsync(new UpdateCourseCommand(course.Id, "Z9", null))).Error!.Type);
        Assert.Equal("course.not_found", (await Update.HandleAsync(new UpdateCourseCommand(Guid.NewGuid(), "A1", null))).Error!.Code);
        Assert.Equal("course.archived", (await Update.HandleAsync(new UpdateCourseCommand(archived.Id, "A2", null))).Error!.Code);
    }

    // ---- Publish ------------------------------------------------------------------------------------------

    private PublishCourseHandler Publish => new(_repository);

    [Fact]
    public async Task Publish_is_refused_for_a_course_without_lessons()
    {
        var course = _repository.AddCourse();

        var result = await Publish.HandleAsync(new PublishCourseCommand(course.Id));

        Assert.Equal("course.no_published_lesson", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal(ContentStatus.Draft, course.Status);
        Assert.Equal(0, _repository.SaveCount);
    }

    [Fact]
    public async Task Publish_is_refused_when_the_only_lessons_are_drafts_or_belong_to_another_course()
    {
        var course = _repository.AddCourse();
        var other = _repository.AddCourse("EN_OTHER");
        _repository.AddLesson(course, published: false);
        _repository.AddLesson(other, published: true);

        var result = await Publish.HandleAsync(new PublishCourseCommand(course.Id));

        Assert.Equal("course.no_published_lesson", result.Error!.Code);
    }

    [Fact]
    public async Task Publish_succeeds_with_a_published_lesson()
    {
        var course = _repository.AddCourse();
        _repository.AddLesson(course, published: true);

        var result = await Publish.HandleAsync(new PublishCourseCommand(course.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(ContentStatus.Published, course.Status);
        Assert.Equal(1, _repository.SaveCount);
    }

    [Fact]
    public async Task Publish_accepts_a_course_in_review()
    {
        var course = _repository.AddCourse();
        course.SubmitForReview();
        _repository.AddLesson(course, published: true);

        Assert.True((await Publish.HandleAsync(new PublishCourseCommand(course.Id))).IsSuccess);
        Assert.Equal(ContentStatus.Published, course.Status);
    }

    [Fact]
    public async Task Publish_is_refused_from_published_and_archived_and_for_unknown_courses()
    {
        var published = _repository.AddCourse("EN_P");
        _repository.AddLesson(published, published: true);
        published.Publish();
        var archived = _repository.AddCourse("EN_A");
        _repository.AddLesson(archived, published: true);
        archived.Archive();

        Assert.Equal("course.invalid_status", (await Publish.HandleAsync(new PublishCourseCommand(published.Id))).Error!.Code);
        Assert.Equal("course.invalid_status", (await Publish.HandleAsync(new PublishCourseCommand(archived.Id))).Error!.Code);
        Assert.Equal("course.not_found", (await Publish.HandleAsync(new PublishCourseCommand(Guid.NewGuid()))).Error!.Code);
    }

    // ---- Archive ------------------------------------------------------------------------------------------

    private ArchiveCourseHandler Archive => new(_repository);

    [Fact]
    public async Task Archive_works_from_any_live_status_and_only_once()
    {
        var draft = _repository.AddCourse("EN_D");
        var published = _repository.AddCourse("EN_P");
        _repository.AddLesson(published, published: true);
        published.Publish();

        Assert.True((await Archive.HandleAsync(new ArchiveCourseCommand(draft.Id))).IsSuccess);
        Assert.True((await Archive.HandleAsync(new ArchiveCourseCommand(published.Id))).IsSuccess);
        Assert.Equal(ContentStatus.Archived, draft.Status);
        Assert.Equal(ContentStatus.Archived, published.Status);

        Assert.Equal("course.already_archived", (await Archive.HandleAsync(new ArchiveCourseCommand(draft.Id))).Error!.Code);
        Assert.Equal("course.not_found", (await Archive.HandleAsync(new ArchiveCourseCommand(Guid.NewGuid()))).Error!.Code);
    }

    // ---- Translations -------------------------------------------------------------------------------------

    private AddCourseTranslationHandler AddTranslation => new(_repository, new AddCourseTranslationCommandValidator());

    private UpdateCourseTranslationHandler UpdateTranslation => new(_repository, new UpdateCourseTranslationCommandValidator());

    [Fact]
    public async Task AddTranslation_stores_it_for_the_language()
    {
        var course = _repository.AddCourse();

        var result = await AddTranslation.HandleAsync(new AddCourseTranslationCommand(course.Id, "VI", "Tiếng Anh A1", "Cơ bản"));

        Assert.True(result.IsSuccess);
        var translation = Assert.Single(course.Translations);
        Assert.Equal(BaseLanguage.Vietnamese, translation.BaseLanguage);
        Assert.Equal("Tiếng Anh A1", translation.Title);
        Assert.Equal(1, _repository.SaveCount);
    }

    [Fact]
    public async Task AddTranslation_refuses_a_second_translation_in_the_same_language()
    {
        var course = _repository.AddCourse();
        course.SetTranslation(BaseLanguage.Vietnamese, "Cũ");

        var result = await AddTranslation.HandleAsync(new AddCourseTranslationCommand(course.Id, "vi", "Mới"));

        Assert.Equal("course.translation_exists", result.Error!.Code);
        Assert.Equal("Cũ", Assert.Single(course.Translations).Title);
    }

    [Theory]
    [InlineData("fr", "Title")]
    [InlineData("vi", "")]
    [InlineData("vi", "   ")]
    public async Task AddTranslation_rejects_invalid_input(string language, string title)
    {
        var course = _repository.AddCourse();

        var result = await AddTranslation.HandleAsync(new AddCourseTranslationCommand(course.Id, language, title));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(course.Translations);
    }

    [Fact]
    public async Task AddTranslation_needs_a_live_course()
    {
        var archived = _repository.AddCourse();
        archived.Archive();

        Assert.Equal("course.not_found", (await AddTranslation.HandleAsync(new AddCourseTranslationCommand(Guid.NewGuid(), "vi", "T"))).Error!.Code);
        Assert.Equal("course.archived", (await AddTranslation.HandleAsync(new AddCourseTranslationCommand(archived.Id, "vi", "T"))).Error!.Code);
    }

    [Fact]
    public async Task UpdateTranslation_replaces_the_existing_text()
    {
        var course = _repository.AddCourse();
        course.SetTranslation(BaseLanguage.Vietnamese, "Cũ", "mô tả cũ");

        var result = await UpdateTranslation.HandleAsync(new UpdateCourseTranslationCommand(course.Id, "vi", "Mới"));

        Assert.True(result.IsSuccess);
        var translation = Assert.Single(course.Translations);
        Assert.Equal("Mới", translation.Title);
        Assert.Null(translation.Description);
    }

    [Fact]
    public async Task UpdateTranslation_of_a_missing_language_is_not_found_not_created()
    {
        var course = _repository.AddCourse();

        var result = await UpdateTranslation.HandleAsync(new UpdateCourseTranslationCommand(course.Id, "vi", "Mới"));

        Assert.Equal("course.translation_not_found", result.Error!.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
        Assert.Empty(course.Translations);
    }
}
