using LinguaBridge.Modules.Learning.Abstractions;

namespace LinguaBridge.Modules.Learning.Grading;

/// <summary>
/// How a question is answered and judged, decided by its content rather than only by its declared type:
/// <list type="bullet">
/// <item>With options, the learner picks one and it is right if that option is flagged correct.</item>
/// <item>Without options, the learner types the answer, and it is right if it matches the question's target text.
/// That text is the answer, so it is never sent to the learner for these questions.</item>
/// <item>Pronunciation questions cannot be judged without speech scoring, so they are not served at all yet.</item>
/// <item>Questions that cannot be judged because they are misconfigured (options but none correct, or a
/// multiple-choice question with no options) are not served either, so no learner is marked wrong for a content error.</item>
/// </list>
/// </summary>
public static class QuestionGrading
{
    private const string MultipleChoice = "MultipleChoice";
    private const string Translate = "Translate";
    private const string FillBlank = "FillBlank";

    public static bool IsAnsweredByChoice(SourceQuestion question) => question.Options.Count > 0;

    public static bool IsGradable(SourceQuestion question)
    {
        if (IsAnsweredByChoice(question))
            return question.Options.Any(o => o.IsCorrect);

        return question.QuestionType is Translate or FillBlank;
    }

    /// <summary>The questions a learner is served and scored on, in lesson order.</summary>
    public static IReadOnlyList<SourceQuestion> Gradable(LessonContent lesson) =>
        lesson.Questions.Where(IsGradable).OrderBy(q => q.OrderIndex).ThenBy(q => q.Id).ToList();

    /// <summary>The learner's view of a question: the answer key is not copied across.</summary>
    public static LearnerQuestionDto ToLearnerDto(SourceQuestion question)
    {
        var choice = IsAnsweredByChoice(question);

        return new LearnerQuestionDto(
            question.Id,
            question.OrderIndex,
            question.QuestionType,
            question.AudioPromptUrl,
            choice ? question.TargetText : null,
            choice ? question.PinyinOrIpa : null,
            question.PromptTranslation,
            question.Options
                .OrderBy(o => o.OrderIndex)
                .Select(o => new LearnerOptionDto(o.Id, o.OrderIndex, o.Text, o.PinyinOrIpa))
                .ToList());
    }

    /// <summary>The first correct option, or the target text for typed questions: what feedback shows after a wrong answer.</summary>
    public static (Guid? OptionId, string? Text) CorrectAnswer(SourceQuestion question)
    {
        if (!IsAnsweredByChoice(question))
            return (null, question.TargetText);

        var correct = question.Options.OrderBy(o => o.OrderIndex).First(o => o.IsCorrect);
        return (correct.Id, correct.Text);
    }
}
