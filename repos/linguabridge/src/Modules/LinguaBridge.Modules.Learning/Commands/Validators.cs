using FluentValidation;

namespace LinguaBridge.Modules.Learning.Commands;

public sealed class StartAttemptCommandValidator : AbstractValidator<StartAttemptCommand>
{
    public StartAttemptCommandValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.LessonId).NotEmpty();
    }
}

public sealed class SubmitResponseCommandValidator : AbstractValidator<SubmitResponseCommand>
{
    /// <summary>A generous ceiling for a typed answer: protects storage without ever rejecting a real sentence.</summary>
    public const int MaxAnswerLength = 2000;

    public SubmitResponseCommandValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.AttemptId).NotEmpty();
        RuleFor(c => c.QuestionId).NotEmpty();
        RuleFor(c => c.TimeSpentMs).GreaterThanOrEqualTo(0);
        RuleFor(c => c.UserInputText).MaximumLength(MaxAnswerLength);
        RuleFor(c => c)
            .Must(c => c.SelectedOptionId.HasValue || !string.IsNullOrWhiteSpace(c.UserInputText))
            .WithName("Answer")
            .WithMessage("Provide selectedOptionId or userInputText.");
    }
}

public sealed class ReviewCardCommandValidator : AbstractValidator<ReviewCardCommand>
{
    public ReviewCardCommandValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.CardId).NotEmpty();
        RuleFor(c => c.Rating).InclusiveBetween(1, 4).WithMessage("'Rating' must be 1 (Again), 2 (Hard), 3 (Good) or 4 (Easy).");
    }
}
