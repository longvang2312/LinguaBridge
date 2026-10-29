using System.Reflection;
using System.Text.Json;
using LinguaBridge.Modules.Learning.Abstractions;
using LinguaBridge.Modules.Learning.Grading;

namespace LinguaBridge.UnitTests.Learning;

public class GradingTests
{
    // ---- Typed answers ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Good morning", "Good morning")]
    [InlineData("good morning", "Good morning")]
    [InlineData("  Good   morning  ", "Good morning")]
    [InlineData("Good morning.", "Good morning")]
    [InlineData("Good morning!", "Good morning.")]
    [InlineData("\"Good morning\"", "Good morning")]
    [InlineData("你好！", "你好")]
    [InlineData("你好", "你好。")]
    [InlineData("ＨＥＬＬＯ", "hello")]
    [InlineData("café", "café")]
    [InlineData("What's your name?", "what's your name")]
    public void Typed_answers_match_despite_case_spacing_punctuation_and_character_forms(string answer, string expected)
    {
        Assert.True(AnswerMatcher.Matches(answer, expected));
    }

    [Theory]
    [InlineData("Good evening", "Good morning")]
    [InlineData("Goodmorning", "Good morning")]
    [InlineData("Good morning everyone", "Good morning")]
    [InlineData("its", "it's")]
    [InlineData("你好吗", "你好")]
    [InlineData("", "Good morning")]
    [InlineData("   ", "Good morning")]
    [InlineData("!!!", "!!!")]
    [InlineData(null, "Good morning")]
    public void Different_words_empty_answers_and_punctuation_only_never_match(string? answer, string expected)
    {
        Assert.False(AnswerMatcher.Matches(answer, expected));
    }

    [Fact]
    public void A_missing_expected_text_matches_nothing()
    {
        Assert.False(AnswerMatcher.Matches("anything", null));
        Assert.False(AnswerMatcher.Matches("anything", ""));
    }

    // ---- What counts as a gradable question ---------------------------------------------------------------

    [Fact]
    public void Choice_and_typed_questions_are_gradable_and_pronunciation_is_not()
    {
        Assert.True(QuestionGrading.IsGradable(LessonData.Choice()));
        Assert.True(QuestionGrading.IsGradable(LessonData.Typed(type: "Translate")));
        Assert.True(QuestionGrading.IsGradable(LessonData.Typed(type: "FillBlank")));
        Assert.False(QuestionGrading.IsGradable(LessonData.Pronounce()));
    }

    [Fact]
    public void A_multiple_choice_question_without_options_cannot_be_judged_so_it_is_not_served()
    {
        Assert.False(QuestionGrading.IsGradable(LessonData.Typed(type: "MultipleChoice")));
    }

    [Fact]
    public void Options_without_a_correct_one_are_a_content_error_and_are_not_served()
    {
        var question = LessonData.Choice() with
        {
            Options = [new SourceOption(Guid.NewGuid(), 0, "A", null, false), new SourceOption(Guid.NewGuid(), 1, "B", null, false)],
        };

        Assert.False(QuestionGrading.IsGradable(question));
    }

    [Fact]
    public void Gradable_questions_come_back_in_lesson_order()
    {
        var third = LessonData.Typed(order: 5);
        var first = LessonData.Choice(order: 0);
        var second = LessonData.Typed(order: 2);
        var lesson = LessonData.Lesson([third, LessonData.Pronounce(order: 1), first, second]);

        Assert.Equal([first.Id, second.Id, third.Id], QuestionGrading.Gradable(lesson).Select(q => q.Id));
    }

    [Fact]
    public void The_answer_after_a_wrong_choice_is_the_correct_option_and_after_a_wrong_typed_answer_the_target_text()
    {
        var choice = QuestionGrading.CorrectAnswer(LessonData.Choice());
        var typed = QuestionGrading.CorrectAnswer(LessonData.Typed(target: "Good morning"));

        Assert.Equal(LessonData.CorrectOption, choice.OptionId);
        Assert.Equal("Hello", choice.Text);
        Assert.Null(typed.OptionId);
        Assert.Equal("Good morning", typed.Text);
    }

    // ---- The learner's view never carries the answer key -------------------------------------------------

    [Fact]
    public void A_learner_dto_has_no_property_that_could_carry_correctness()
    {
        var forbidden = new[] { "iscorrect", "correct", "answer", "solution", "key" };

        foreach (var type in new[] { typeof(LearnerOptionDto), typeof(LearnerQuestionDto), typeof(LearnerQuizDto) })
        {
            var names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name.ToLowerInvariant());
            Assert.DoesNotContain(names, name => forbidden.Any(name.Contains));
        }
    }

    [Fact]
    public void The_serialised_quiz_contains_no_correctness_flag_at_all()
    {
        var question = QuestionGrading.ToLearnerDto(LessonData.Choice());

        var json = JsonSerializer.Serialize(new LearnerQuizDto(Guid.NewGuid(), Guid.NewGuid(), 20, [question]));

        Assert.DoesNotContain("correct", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, question.Options.Count);
    }

    [Fact]
    public void A_choice_question_shows_its_text_but_a_typed_question_hides_the_text_that_is_its_answer()
    {
        var choice = QuestionGrading.ToLearnerDto(LessonData.Choice(target: "你好"));
        var typed = QuestionGrading.ToLearnerDto(LessonData.Typed(target: "Good morning"));

        Assert.Equal("你好", choice.TargetText);
        Assert.Equal("nǐ hǎo", choice.PinyinOrIpa);

        Assert.Null(typed.TargetText);
        Assert.Null(typed.PinyinOrIpa);
        Assert.Equal("Chào buổi sáng", typed.PromptTranslation);
        Assert.Empty(typed.Options);
    }

    [Fact]
    public void Options_are_shown_in_their_order()
    {
        var dto = QuestionGrading.ToLearnerDto(LessonData.Choice());

        Assert.Equal([0, 1, 2], dto.Options.Select(o => o.OrderIndex));
        Assert.Equal(["Goodbye", "Hello", "Thanks"], dto.Options.Select(o => o.Text));
    }
}
