using FluentValidation;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Learning.Abstractions;
using LinguaBridge.Modules.Learning.Entities;

namespace LinguaBridge.Modules.Learning.Commands;

/// <param name="Rating">1 Again, 2 Hard, 3 Good, 4 Easy.</param>
public sealed record ReviewCardCommand(Guid UserId, Guid CardId, int Rating);

/// <summary>
/// Applies the learner's self-assessment to a card that is due and schedules its next review (SM-2).
/// A card that is not due yet is refused: reviewing early would stretch its interval without the forgetting
/// the schedule is built on.
/// </summary>
public sealed class ReviewCardHandler(ILearningRepository repository, IDateTimeProvider clock, IValidator<ReviewCardCommand> validator)
{
    public async Task<Result<ReviewResultDto>> HandleAsync(ReviewCardCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        var card = await repository.GetCardAsync(command.UserId, command.CardId, cancellationToken);
        if (card is null)
            return Error.NotFound("card.not_found", $"Card {command.CardId} was not found.");

        var today = clock.TodayUtc();
        if (!card.IsDueOn(today))
            return Error.Conflict("srs.card_not_due", $"The card is not due until {card.NextReviewDate:yyyy-MM-dd}.");

        var rating = (ReviewRating)command.Rating;
        repository.Add(card.ApplyRating(rating, today, clock.UtcNow));

        if (await repository.TrySaveAsync(cancellationToken) is { } conflict)
            return conflict;

        return new ReviewResultDto(card.Id, command.Rating, card.EaseFactor, card.IntervalDays, card.Repetitions, card.NextReviewDate, card.State.ToString());
    }
}
