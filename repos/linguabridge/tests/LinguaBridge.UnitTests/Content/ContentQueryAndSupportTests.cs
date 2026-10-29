using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content;
using LinguaBridge.Modules.Content.Abstractions;
using LinguaBridge.Modules.Content.Entities;
using LinguaBridge.Modules.Content.Queries;

namespace LinguaBridge.UnitTests.Content;

public class ContentQueryAndSupportTests
{
    private readonly FakeContentReadModel _readModel = new();

    private static CourseSummaryDto Summary(string code) =>
        new(Guid.NewGuid(), code, "en", "A1", "Published", 1, null, "Title", null, "vi", DateTimeOffset.UtcNow);

    private static LessonDto Lesson() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 0, "Standard", "Published", 20, 1, "T", null, "vi");

    // ---- Queries ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Published_courses_query_passes_both_languages_through()
    {
        _readModel.Courses = [Summary("A"), Summary("B")];

        var result = await new GetPublishedCoursesHandler(_readModel)
            .HandleAsync(new GetPublishedCoursesQuery(BaseLanguage.Vietnamese, TargetLanguage.Chinese));

        Assert.Equal(2, result.Value.Count);
        Assert.Equal(["published:Vietnamese:Chinese"], _readModel.Calls);
    }

    [Fact]
    public async Task All_courses_query_defaults_to_every_target_language()
    {
        await new GetAllCoursesHandler(_readModel).HandleAsync(new GetAllCoursesQuery(null));
        await new GetAllCoursesHandler(_readModel).HandleAsync(new GetAllCoursesQuery(TargetLanguage.English, BaseLanguage.English));

        Assert.Equal(["all:any:Vietnamese", "all:English:English"], _readModel.Calls);
    }

    [Fact]
    public async Task Course_detail_carries_the_visibility_flag_and_maps_absence_to_not_found()
    {
        var id = Guid.NewGuid();
        var handler = new GetCourseDetailHandler(_readModel);

        var missing = await handler.HandleAsync(new GetCourseDetailQuery(id, BaseLanguage.English, IncludeUnpublished: false));
        _readModel.Detail = new CourseDetailDto(Summary("A"), []);
        var found = await handler.HandleAsync(new GetCourseDetailQuery(id, BaseLanguage.English, IncludeUnpublished: true));

        Assert.Equal("course.not_found", missing.Error!.Code);
        Assert.True(found.IsSuccess);
        Assert.Equal([$"detail:{id}:English:False", $"detail:{id}:English:True"], _readModel.Calls);
    }

    [Fact]
    public async Task Lessons_query_distinguishes_an_empty_course_from_a_missing_one()
    {
        var id = Guid.NewGuid();
        var handler = new GetLessonsHandler(_readModel);

        _readModel.Lessons = null;
        var missing = await handler.HandleAsync(new GetLessonsQuery(id, BaseLanguage.Vietnamese, false));
        _readModel.Lessons = [];
        var empty = await handler.HandleAsync(new GetLessonsQuery(id, BaseLanguage.Vietnamese, false));
        _readModel.Lessons = [Lesson()];
        var one = await handler.HandleAsync(new GetLessonsQuery(id, BaseLanguage.Vietnamese, true));

        Assert.Equal("course.not_found", missing.Error!.Code);
        Assert.Empty(empty.Value);
        Assert.Single(one.Value);
    }

    [Fact]
    public async Task Preview_query_maps_absence_to_not_found()
    {
        var handler = new PreviewLessonHandler(_readModel);

        var missing = await handler.HandleAsync(new PreviewLessonQuery(Guid.NewGuid(), BaseLanguage.Vietnamese));
        _readModel.Preview = new LessonPreviewDto(Lesson(), []);
        var found = await handler.HandleAsync(new PreviewLessonQuery(Guid.NewGuid(), BaseLanguage.Vietnamese));

        Assert.Equal("lesson.not_found", missing.Error!.Code);
        Assert.True(found.IsSuccess);
    }

    // ---- Parsing ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en", true, TargetLanguage.English)]
    [InlineData(" ZH ", true, TargetLanguage.Chinese)]
    [InlineData("fr", false, default(TargetLanguage))]
    [InlineData("", false, default(TargetLanguage))]
    [InlineData(null, false, default(TargetLanguage))]
    public void Target_language_codes_parse_strictly(string? code, bool ok, TargetLanguage expected)
    {
        Assert.Equal(ok, ContentParsing.TryParseTargetLanguage(code, out var language));
        Assert.Equal(expected, language);
    }

    [Theory]
    [InlineData("vi", true)]
    [InlineData("EN", true)]
    [InlineData("zh", false)]
    [InlineData("1", false)]
    public void Base_language_codes_parse_strictly(string code, bool ok)
    {
        Assert.Equal(ok, ContentParsing.TryParseBaseLanguage(code, out _));
    }

    [Theory]
    [InlineData("A1", true, LanguageLevel.A1)]
    [InlineData("c2", true, LanguageLevel.C2)]
    [InlineData("HSK1", true, LanguageLevel.Hsk1)]
    [InlineData("hsk6", true, LanguageLevel.Hsk6)]
    [InlineData("HSK7", false, default(LanguageLevel))]
    [InlineData("101", false, default(LanguageLevel))]
    [InlineData("1", false, default(LanguageLevel))]
    [InlineData("A1,B1", false, default(LanguageLevel))]
    [InlineData("", false, default(LanguageLevel))]
    public void Level_codes_parse_strictly(string code, bool ok, LanguageLevel expected)
    {
        Assert.Equal(ok, ContentParsing.TryParseLevel(code, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("Standard", true)]
    [InlineData("pinyin", true)]
    [InlineData("Quiz", false)]
    [InlineData("0", false)]
    public void Lesson_types_parse_by_name_only(string name, bool ok)
    {
        Assert.Equal(ok, ContentParsing.TryParseLessonType(name, out _));
    }

    [Theory]
    [InlineData("MultipleChoice", true)]
    [InlineData("fillblank", true)]
    [InlineData("Essay", false)]
    [InlineData("2", false)]
    public void Question_types_parse_by_name_only(string name, bool ok)
    {
        Assert.Equal(ok, ContentParsing.TryParseQuestionType(name, out _));
    }

    // ---- Translation fallback ----------------------------------------------------------------------------

    private sealed record Tr(BaseLanguage Language, string Text);

    private static readonly Tr Vi = new(BaseLanguage.Vietnamese, "xin chào");
    private static readonly Tr En = new(BaseLanguage.English, "hello");

    [Fact]
    public void Resolver_prefers_the_requested_language()
    {
        Assert.Same(Vi, TranslationResolver.Pick([En, Vi], BaseLanguage.Vietnamese, t => t.Language));
        Assert.Same(En, TranslationResolver.Pick([Vi, En], BaseLanguage.English, t => t.Language));
    }

    [Fact]
    public void Resolver_uses_english_when_asked_for_vietnamese_and_only_english_exists()
    {
        Assert.Same(En, TranslationResolver.Pick([En], BaseLanguage.Vietnamese, t => t.Language));
    }

    [Fact]
    public void Resolver_returns_whatever_exists_when_neither_requested_nor_english_is_there()
    {
        Assert.Same(Vi, TranslationResolver.Pick([Vi], BaseLanguage.English, t => t.Language));
    }

    [Fact]
    public void Resolver_returns_null_when_there_are_no_translations()
    {
        Assert.Null(TranslationResolver.Pick(Array.Empty<Tr>(), BaseLanguage.Vietnamese, t => t.Language));
    }

    // ---- Result -------------------------------------------------------------------------------------------

    [Fact]
    public void Reading_the_value_of_a_failed_result_is_a_programming_error()
    {
        Result<int> failed = Error.NotFound("x.not_found", "nope");

        Assert.False(failed.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => failed.Value);
    }

    [Fact]
    public void Values_and_errors_convert_implicitly()
    {
        Result<int> ok = 5;
        Result plain = Error.Conflict("x.conflict", "no");

        Assert.Equal(5, ok.Value);
        Assert.Equal(ErrorType.Conflict, plain.Error!.Type);
        Assert.True(Result.Success().IsSuccess);
    }

    // ---- Domain transitions -------------------------------------------------------------------------------

    [Fact]
    public void Course_moves_draft_to_review_to_published_to_archived_and_no_further()
    {
        var course = new Course("EN_X", TargetLanguage.English, LanguageLevel.A1);

        course.SubmitForReview();
        Assert.Equal(ContentStatus.Review, course.Status);
        Assert.Throws<InvalidOperationException>(course.SubmitForReview);

        course.Publish();
        Assert.Equal(ContentStatus.Published, course.Status);
        Assert.Throws<InvalidOperationException>(course.Publish);

        course.Archive();
        Assert.True(course.IsArchived);
        Assert.Throws<InvalidOperationException>(course.Archive);
        Assert.Throws<InvalidOperationException>(course.Publish);
        Assert.Throws<InvalidOperationException>(() => course.UpdateDetails(LanguageLevel.A2, null));
    }

    [Fact]
    public void Course_update_keeps_the_level_inside_its_language_and_bumps_the_timestamp()
    {
        var course = new Course("ZH_X", TargetLanguage.Chinese, LanguageLevel.Hsk1);
        var before = course.UpdatedAt;

        Assert.Throws<ArgumentException>(() => course.UpdateDetails(LanguageLevel.B1, null));
        course.UpdateDetails(LanguageLevel.Hsk3, null);

        Assert.Equal(LanguageLevel.Hsk3, course.Level);
        Assert.True(course.UpdatedAt >= before);
    }

    [Fact]
    public void Lesson_publishes_from_draft_once_and_archives_once()
    {
        var lesson = new Lesson(Guid.NewGuid(), 0, LessonType.Standard);

        lesson.Publish();
        Assert.Equal(ContentStatus.Published, lesson.Status);
        Assert.Throws<InvalidOperationException>(lesson.Publish);

        lesson.Archive();
        Assert.Equal(ContentStatus.Archived, lesson.Status);
        Assert.Throws<InvalidOperationException>(lesson.Archive);
        Assert.Throws<InvalidOperationException>(lesson.Publish);
    }
}
