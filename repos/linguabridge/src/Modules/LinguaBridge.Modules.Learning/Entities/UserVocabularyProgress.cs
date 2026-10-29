using LinguaBridge.Domain.Common;
using LinguaBridge.Domain.Enums;

namespace LinguaBridge.Modules.Learning.Entities;

/// <summary>
/// A learner's SM-2 card for one vocabulary item: when it is next due and how easy it has been.
/// One card per (UserId, VocabularyId). The scheduling algorithm itself is not implemented yet.
/// </summary>
public class UserVocabularyProgress : AggregateRoot
{
    /// <summary>Standard SM-2 starting ease factor.</summary>
    public const double DefaultEaseFactor = 2.5;

    // Required by EF Core.
    private UserVocabularyProgress() { }

    public UserVocabularyProgress(Guid userId, Guid vocabularyId, TargetLanguage targetLanguage, DateOnly firstReviewDate)
    {
        UserId = Guard.RequiredId(userId, nameof(userId));
        VocabularyId = Guard.RequiredId(vocabularyId, nameof(vocabularyId));
        TargetLanguage = targetLanguage;
        Repetitions = 0;
        EaseFactor = DefaultEaseFactor;
        IntervalDays = 0;
        NextReviewDate = firstReviewDate;
        State = SrsState.Learning;
    }

    public Guid UserId { get; private set; }

    /// <summary>Id of the vocabulary item or question being memorised (owned by Content).</summary>
    public Guid VocabularyId { get; private set; }

    public TargetLanguage TargetLanguage { get; private set; }
    public int Repetitions { get; private set; }
    public double EaseFactor { get; private set; }
    public int IntervalDays { get; private set; }
    public DateOnly NextReviewDate { get; private set; }
    public DateTimeOffset? LastReviewDate { get; private set; }
    public SrsState State { get; private set; }
}
