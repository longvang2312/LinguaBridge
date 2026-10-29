using FluentValidation;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Content.Abstractions;
using LinguaBridge.Modules.Content.Entities;

namespace LinguaBridge.Modules.Content.Commands;

public sealed record QuestionTranslationInput(string BaseLanguage, string PromptTranslation, string? Explanation = null);

/// <param name="OrderIndex">Position in the lesson; null appends after the last question.</param>
/// <param name="QuestionType"><c>MultipleChoice</c>, <c>Translate</c>, <c>FillBlank</c> or <c>Pronounce</c>.</param>
/// <param name="Translations">Prompts in the learner's languages; at most one per language. Options are added separately.</param>
public sealed record AddQuestionCommand(
    Guid LessonId,
    string QuestionType,
    string TargetText,
    int? OrderIndex = null,
    string? PinyinOrIpa = null,
    string? AudioPromptUrl = null,
    string? GrammarNotes = null,
    IReadOnlyList<QuestionTranslationInput>? Translations = null);

public sealed record AddQuestionOptionCommand(Guid QuestionId, string OptionText, bool IsCorrect, string? PinyinOrIpa = null);

public sealed class AddQuestionHandler(IContentRepository repository, IValidator<AddQuestionCommand> validator)
{
    public async Task<Result<Guid>> HandleAsync(AddQuestionCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        var lesson = await repository.GetLessonAsync(command.LessonId, cancellationToken);
        if (lesson is null)
            return CommandSupport.LessonNotFound(command.LessonId);

        ContentParsing.TryParseQuestionType(command.QuestionType, out var questionType);
        var orderIndex = command.OrderIndex ?? await repository.GetNextQuestionOrderIndexAsync(lesson.Id, cancellationToken);

        var question = new Question(
            lesson.Id,
            orderIndex,
            questionType,
            command.TargetText,
            command.PinyinOrIpa,
            command.AudioPromptUrl,
            command.GrammarNotes);

        foreach (var translation in command.Translations ?? [])
        {
            ContentParsing.TryParseBaseLanguage(translation.BaseLanguage, out var language);
            question.SetTranslation(language, translation.PromptTranslation, translation.Explanation);
        }

        repository.Add(question);

        await repository.SaveChangesAsync(cancellationToken);
        return question.Id;
    }
}

public sealed class AddQuestionOptionHandler(IContentRepository repository, IValidator<AddQuestionOptionCommand> validator)
{
    public async Task<Result<Guid>> HandleAsync(AddQuestionOptionCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        var question = await repository.GetQuestionAsync(command.QuestionId, cancellationToken);
        if (question is null)
            return CommandSupport.QuestionNotFound(command.QuestionId);

        var option = question.AddOption(command.OptionText, command.IsCorrect, command.PinyinOrIpa);

        await repository.SaveChangesAsync(cancellationToken);
        return option.Id;
    }
}
