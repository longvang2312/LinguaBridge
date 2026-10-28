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

    public List<string> AdminPortalRedirectUris { get; set; } = new();
    public List<string> AdminPortalPostLogoutRedirectUris { get; set; } = new();
}
