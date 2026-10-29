namespace LinguaBridge.Modules.Learning.Entities;

/// <summary>Where a vocabulary card is in the SRS cycle. Stored by name.</summary>
public enum SrsState
{
    Learning,
    Review,
    Graduated,
}

/// <summary>The learner's self-assessment for one review. Stored as its number (1-4).</summary>
public enum ReviewRating
{
    Again = 1,
    Hard = 2,
    Good = 3,
    Easy = 4,
}
