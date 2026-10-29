namespace LinguaBridge.Modules.Content.Entities;

/// <summary>Lifecycle shared by courses and lessons. Stored by name.</summary>
public enum ContentStatus
{
    Draft,
    Review,
    Published,
    Archived,
}

public enum LessonType
{
    Standard,
    Vocabulary,
    Pinyin,
    Grammar,
}

public enum QuestionType
{
    MultipleChoice,
    Translate,
    FillBlank,
    Pronounce,
}
