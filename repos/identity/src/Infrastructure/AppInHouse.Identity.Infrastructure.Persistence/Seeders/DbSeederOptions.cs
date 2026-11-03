namespace AppInHouse.Identity.Infrastructure.Persistence.Seeders;

/// <summary>
/// Bound from the <c>Seeding</c> configuration section. There are deliberately no built-in credentials:
/// every secret must be supplied by configuration (development defaults live in appsettings.Development.json).
/// </summary>
public class DbSeederOptions
{
    public const string SectionName = "Seeding";

    /// <summary>Whether the seeder runs at STS startup.</summary>
    public bool Enabled { get; set; }

    public string AdminEmail { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;

    /// <summary>Secret of the confidential <c>internal-service</c> client.</summary>
    public string InternalServiceSecret { get; set; } = string.Empty;

    /// <summary>
    /// Secret of the <c>identity_admin_api</c> resource-server client the Admin API uses to introspect access tokens.
    /// Optional: the client is only seeded when a secret is configured.
    /// </summary>
    public string AdminApiClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Secret of the <c>linguabridge_api</c> resource-server client the LinguaBridge API uses to introspect access tokens.
    /// Optional: the scope and client are only seeded when a secret is configured.
    /// </summary>
    public string LinguaBridgeApiClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Whether to seed the <c>linguabridge-app</c> public client (Authorization Code + PKCE) for the learners' app.
    /// Off unless asked for, and only meaningful together with <see cref="LinguaBridgeApiClientSecret"/> because the client
    /// asks for the <c>linguabridge_api</c> scope, which only exists then.
    /// </summary>
    public bool LinguaBridgeAppEnabled { get; set; }

    /// <summary>Redirect addresses of <c>linguabridge-app</c>. When empty the local development ones are used (ports 3000 and 5173).</summary>
    public List<string> LinguaBridgeAppRedirectUris { get; set; } = new();

    public List<string> AdminPortalRedirectUris { get; set; } = new();
    public List<string> AdminPortalPostLogoutRedirectUris { get; set; } = new();
}
