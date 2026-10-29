namespace LinguaBridge.Infrastructure.Persistence;

/// <summary>Database settings. One PostgreSQL database; each module owns its own schema inside it.</summary>
public sealed class PersistenceOptions
{
    public const string ConnectionStringName = "LinguaBridgeDb";

    /// <summary>Null or empty until a connection string is configured; the DbContext then fails on first use.</summary>
    public string? ConnectionString { get; init; }
}
