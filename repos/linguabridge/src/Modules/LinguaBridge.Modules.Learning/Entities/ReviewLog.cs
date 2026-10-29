using LinguaBridge.Domain.Common;

namespace LinguaBridge.Modules.Learning.Entities;

/// <summary>Audit trail of one SRS review: the rating given and how it changed the card's schedule.</summary>
public class ReviewLog : BaseEntity
{
    // Required by EF Core.
    private ReviewLog() { }

    public ReviewLog(
        Guid userVocabularyId,
        ReviewRating rating,
        double oldEaseFactor,
        double newEaseFactor,
        int oldIntervalDays,
        int newIntervalDays)
    {
        if (!Enum.IsDefined(rating))
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "Rating must be between 1 (Again) and 4 (Easy).");

        UserVocabularyId = Guard.RequiredId(userVocabularyId, nameof(userVocabularyId));
        Rating = rating;
        OldEaseFactor = oldEaseFactor;
        NewEaseFactor = newEaseFactor;
        OldIntervalDays = Guard.NonNegative(oldIntervalDays, nameof(oldIntervalDays));
        NewIntervalDays = Guard.NonNegative(newIntervalDays, nameof(newIntervalDays));
        ReviewedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserVocabularyId { get; private set; }
    public ReviewRating Rating { get; private set; }
    public double OldEaseFactor { get; private set; }
    public double NewEaseFactor { get; private set; }
    public int OldIntervalDays { get; private set; }
    public int NewIntervalDays { get; private set; }
    public DateTimeOffset ReviewedAt { get; private set; }
}
