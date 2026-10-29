using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content.Commands;
using LinguaBridge.Modules.Content.Entities;

namespace LinguaBridge.UnitTests.Content;

public class LessonAndQuestionCommandTests
{
    private readonly FakeContentRepository _repository = new();

    private CreateLessonHandler CreateLesson => new(_repository, new CreateLessonCommandValidator());

    private PublishLessonHandler PublishLesson => new(_repository);

    private AddLessonTranslationHandler AddLessonTranslation => new(_repository, new AddLessonTranslationCommandValidator());

    private AddQuestionHandler AddQuestion => new(_repository, new AddQuestionCommandValidator());

    private AddQuestionOptionHandler AddOption => new(_repository, new AddQuestionOptionCommandValidator());

    // ---- Lessons ------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateLesson_makes_a_draft_with_the_default_xp()
    {
        var course = _repository.AddCourse();

        var result = await CreateLesson.HandleAsync(new CreateLessonCommand(course.Id, "Grammar"));

        Assert.True(result.IsSuccess);
        var lesson = _repository.Lessons[result.Value];
        Assert.Equal(ContentStatus.Draft, lesson.Status);
        Assert.Equal(LessonType.Grammar, lesson.LessonType);
        Assert.Equal(Lesson.DefaultXpReward, lesson.XpReward);
        Assert.Equal(course.Id, lesson.CourseId);
    }

    [Fact]
    public async Task CreateLesson_appends_after_the_last_lesson_of_that_course_only()
    {
        var course = _repository.AddCourse();
        var other = _repository.AddCourse("EN_OTHER");
        _repository.AddLesson(course, orderIndex: 0);
        _repository.AddLesson(course, orderIndex: 4);
        _repository.AddLesson(other, orderIndex: 9);

        var appended = await CreateLesson.HandleAsync(new CreateLessonCommand(course.Id, "Standard"));
        var firstOfOther = await CreateLesson.HandleAsync(new CreateLessonCommand(_repository.AddCourse("EN_NEW").Id, "Standard"));

        Assert.Equal(5, _repository.Lessons[appended.Value].OrderIndex);
        Assert.Equal(0, _repository.Lessons[firstOfOther.Value].OrderIndex);
    }

    [Fact]
    public async Task CreateLesson_honours_an_explicit_position_and_xp()
    {
        var course = _repository.AddCourse();

        var result = await CreateLesson.HandleAsync(new CreateLessonCommand(course.Id, "vocabulary", OrderIndex: 2, XpReward: 50));

        var lesson = _repository.Lessons[result.Value];
        Assert.Equal(2, lesson.OrderIndex);
        Assert.Equal(50, lesson.XpReward);
    }

    [Theory]
    [InlineData("Quiz", null, null, "LessonType")]
    [InlineData("", null, null, "LessonType")]
    [InlineData("1", null, null, "LessonType")]
    [InlineData("Standard", -1, null, "OrderIndex")]
    [InlineData("Standard", null, -5, "XpReward")]
    public async Task CreateLesson_rejects_invalid_input(string type, int? order, int? xp, string invalidField)
    {
        var course = _repository.AddCourse();

        var result = await CreateLesson.HandleAsync(new CreateLessonCommand(course.Id, type, order, xp));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Contains(invalidField, result.Error.Details!.Keys);
        Assert.Empty(_repository.Lessons);
    }

    [Fact]
    public async Task CreateLesson_needs_a_live_course()
    {
        var archived = _repository.AddCourse();
        archived.Archive();

        Assert.Equal("course.not_found", (await CreateLesson.HandleAsync(new CreateLessonCommand(Guid.NewGuid(), "Standard"))).Error!.Code);
        Assert.Equal("course.archived", (await CreateLesson.HandleAsync(new CreateLessonCommand(archived.Id, "Standard"))).Error!.Code);
    }

    [Fact]
    public async Task PublishLesson_publishes_a_draft_once()
    {
        var lesson = _repository.AddLesson(_repository.AddCourse());

        Assert.True((await PublishLesson.HandleAsync(new PublishLessonCommand(lesson.Id))).IsSuccess);
        Assert.Equal(ContentStatus.Published, lesson.Status);

        var again = await PublishLesson.HandleAsync(new PublishLessonCommand(lesson.Id));
        Assert.Equal("lesson.invalid_status", again.Error!.Code);
    }

    [Fact]
    public async Task PublishLesson_is_refused_in_an_archived_course_and_for_unknown_lessons()
    {
        var course = _repository.AddCourse();
        var lesson = _repository.AddLesson(course);
        course.Archive();

        Assert.Equal("course.archived", (await PublishLesson.HandleAsync(new PublishLessonCommand(lesson.Id))).Error!.Code);
        Assert.Equal("lesson.not_found", (await PublishLesson.HandleAsync(new PublishLessonCommand(Guid.NewGuid()))).Error!.Code);
        Assert.Equal(ContentStatus.Draft, lesson.Status);
    }

    [Fact]
    public async Task Publishing_the_only_lesson_unblocks_publishing_the_course()
    {
        var course = _repository.AddCourse();
        var lesson = _repository.AddLesson(course);
        var publishCourse = new PublishCourseHandler(_repository);

        Assert.False((await publishCourse.HandleAsync(new PublishCourseCommand(course.Id))).IsSuccess);
        await PublishLesson.HandleAsync(new PublishLessonCommand(lesson.Id));

        Assert.True((await publishCourse.HandleAsync(new PublishCourseCommand(course.Id))).IsSuccess);
    }

    [Fact]
    public async Task AddLessonTranslation_adds_once_per_language()
    {
        var lesson = _repository.AddLesson(_repository.AddCourse());

        Assert.True((await AddLessonTranslation.HandleAsync(new AddLessonTranslationCommand(lesson.Id, "vi", "Chào hỏi", "Hướng dẫn"))).IsSuccess);
        var duplicate = await AddLessonTranslation.HandleAsync(new AddLessonTranslationCommand(lesson.Id, "VI", "Khác"));

        Assert.Equal("lesson.translation_exists", duplicate.Error!.Code);
        Assert.Equal("Chào hỏi", Assert.Single(lesson.Translations).Title);
    }

    [Fact]
    public async Task AddLessonTranslation_rejects_bad_input_and_unknown_lessons()
    {
        var lesson = _repository.AddLesson(_repository.AddCourse());

        Assert.Equal(ErrorType.Validation, (await AddLessonTranslation.HandleAsync(new AddLessonTranslationCommand(lesson.Id, "xx", "T"))).Error!.Type);
        Assert.Equal(ErrorType.Validation, (await AddLessonTranslation.HandleAsync(new AddLessonTranslationCommand(lesson.Id, "vi", " "))).Error!.Type);
        Assert.Equal("lesson.not_found", (await AddLessonTranslation.HandleAsync(new AddLessonTranslationCommand(Guid.NewGuid(), "vi", "T"))).Error!.Code);
    }

    // ---- Questions ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AddQuestion_stores_text_and_translations_and_appends_to_the_lesson()
    {
        var lesson = _repository.AddLesson(_repository.AddCourse());
        _repository.AddQuestion(lesson, orderIndex: 0);

        var result = await AddQuestion.HandleAsync(new AddQuestionCommand(
            lesson.Id,
            "multiplechoice",
            "你好",
            PinyinOrIpa: "nǐ hǎo",
            Translations: [new QuestionTranslationInput("vi", "Xin chào"), new QuestionTranslationInput("en", "Hello", "A greeting")]));

        Assert.True(result.IsSuccess);
        var question = _repository.Questions[result.Value];
        Assert.Equal(1, question.OrderIndex);
        Assert.Equal(QuestionType.MultipleChoice, question.QuestionType);
        Assert.Equal("你好", question.TargetText);
        Assert.Equal("nǐ hǎo", question.PinyinOrIpa);
        Assert.Equal(2, question.Translations.Count);
        Assert.Empty(question.Options);
    }

    [Fact]
    public async Task AddQuestion_works_without_translations()
    {
        var lesson = _repository.AddLesson(_repository.AddCourse());

        var result = await AddQuestion.HandleAsync(new AddQuestionCommand(lesson.Id, "Translate", "Good morning"));

        Assert.True(result.IsSuccess);
        Assert.Empty(_repository.Questions[result.Value].Translations);
    }

    [Theory]
    [InlineData("Essay", "text", "QuestionType")]
    [InlineData("Translate", "", "TargetText")]
    [InlineData("Translate", "  ", "TargetText")]
    public async Task AddQuestion_rejects_invalid_input(string type, string text, string invalidField)
    {
        var lesson = _repository.AddLesson(_repository.AddCourse());

        var result = await AddQuestion.HandleAsync(new AddQuestionCommand(lesson.Id, type, text));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Contains(invalidField, result.Error.Details!.Keys);
        Assert.Empty(_repository.Questions);
    }

    [Fact]
    public async Task AddQuestion_rejects_a_language_listed_twice_or_unknown_or_without_text()
    {
        var lesson = _repository.AddLesson(_repository.AddCourse());

        var twice = await AddQuestion.HandleAsync(new AddQuestionCommand(lesson.Id, "Translate", "Hi",
            Translations: [new QuestionTranslationInput("vi", "Chào"), new QuestionTranslationInput("VI", "Xin chào")]));
        var unknown = await AddQuestion.HandleAsync(new AddQuestionCommand(lesson.Id, "Translate", "Hi",
            Translations: [new QuestionTranslationInput("fr", "Salut")]));
        var blank = await AddQuestion.HandleAsync(new AddQuestionCommand(lesson.Id, "Translate", "Hi",
            Translations: [new QuestionTranslationInput("vi", " ")]));

        Assert.All([twice, unknown, blank], r => Assert.Equal(ErrorType.Validation, r.Error!.Type));
        Assert.Empty(_repository.Questions);
    }

    [Fact]
    public async Task AddQuestion_needs_an_existing_lesson()
    {
        var result = await AddQuestion.HandleAsync(new AddQuestionCommand(Guid.NewGuid(), "Translate", "Hi"));

        Assert.Equal("lesson.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task AddOption_appends_options_in_order_and_keeps_the_correct_flag()
    {
        var question = _repository.AddQuestion(_repository.AddLesson(_repository.AddCourse()));

        var first = await AddOption.HandleAsync(new AddQuestionOptionCommand(question.Id, "Hello", IsCorrect: true));
        var second = await AddOption.HandleAsync(new AddQuestionOptionCommand(question.Id, "Goodbye", IsCorrect: false, PinyinOrIpa: "ɡʊdˈbaɪ"));

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal([0, 1], question.Options.Select(o => o.OrderIndex));
        Assert.True(question.Options.Single(o => o.Id == first.Value).IsCorrect);
        Assert.False(question.Options.Single(o => o.Id == second.Value).IsCorrect);
        Assert.Equal(2, _repository.SaveCount);
    }

    [Fact]
    public async Task AddOption_rejects_blank_text_and_unknown_questions()
    {
        var question = _repository.AddQuestion(_repository.AddLesson(_repository.AddCourse()));

        Assert.Equal(ErrorType.Validation, (await AddOption.HandleAsync(new AddQuestionOptionCommand(question.Id, " ", true))).Error!.Type);
        Assert.Equal("question.not_found", (await AddOption.HandleAsync(new AddQuestionOptionCommand(Guid.NewGuid(), "x", true))).Error!.Code);
        Assert.Empty(question.Options);
    }
}
