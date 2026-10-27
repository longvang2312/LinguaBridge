namespace AppInHouse.Identity.Domain.Common;

/// <summary>Validation helpers used to enforce entity invariants at construction time.</summary>
internal static class Guard
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
}
