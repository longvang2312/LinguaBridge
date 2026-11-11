namespace AppInHouse.SharedServices.Application.Security;

/// <summary>
/// What a client sees in place of a stored secret. The plain text never leaves the service after it is saved: a screen shows the mask,
/// and typing a new value is the only way to change the secret.
/// </summary>
public static class SecretMask
{
    public const string Value = "••••••••";

    public static string For(bool hasSecret) => hasSecret ? Value : string.Empty;

    /// <summary>True for the mask itself, which a form sends back when the user left the secret field alone.</summary>
    public static bool IsMask(string? text) => string.Equals(text, Value, StringComparison.Ordinal);
}
