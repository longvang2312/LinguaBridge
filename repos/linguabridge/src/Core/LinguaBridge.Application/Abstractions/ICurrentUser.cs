namespace LinguaBridge.Application.Abstractions;

/// <summary>
/// The caller of the current request, so modules never touch <c>HttpContext</c>. Carries identity only:
/// what the caller is entitled to (Free/Vip) is looked up in the Billing module, not read from the token.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>The <c>sub</c> claim; null when absent or not a Guid (e.g. a <c>client_credentials</c> token).</summary>
    Guid? UserId { get; }

    /// <summary>The OAuth client the token was issued to (<c>client_id</c>, else <c>azp</c>).</summary>
    string? ClientId { get; }

    bool IsInRole(string role);
}
