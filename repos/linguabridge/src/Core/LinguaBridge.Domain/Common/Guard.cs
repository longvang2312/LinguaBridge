namespace LinguaBridge.Domain.Common;

/// <summary>Validation helpers used to enforce entity invariants at construction time.</summary>
public static class Guard
{
    /// <summary>Returns the trimmed value; throws when it is null/blank or longer than <paramref name="maxLength"/>.</summary>
    public static string Required(string? value, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} must not be empty.", paramName);

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{paramName} must not exceed {maxLength} characters.", paramName);

        return trimmed;
    }

    /// <summary>Returns the trimmed value or null when blank; throws when longer than <paramref name="maxLength"/>.</summary>
    public static string? Optional(string? value, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{paramName} must not exceed {maxLength} characters.", paramName);

        return trimmed;
    }

    public static Guid RequiredId(Guid value, string paramName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException($"{paramName} must not be an empty Guid.", paramName);

        return value;
    }

    public static int NonNegative(int value, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, paramName);
        return value;
    }

    /// <summary>Returns <paramref name="value"/> when it lies in [<paramref name="min"/>, <paramref name="max"/>], inclusive.</summary>
    public static int Range(int value, int min, int max, string paramName)
    {
        if (min > max)
            throw new ArgumentException($"min ({min}) must not exceed max ({max}).", nameof(min));

        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} must be between {min} and {max}.");

        return value;
    }
}
