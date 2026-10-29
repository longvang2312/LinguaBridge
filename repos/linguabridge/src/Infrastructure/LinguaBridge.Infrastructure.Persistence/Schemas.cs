namespace LinguaBridge.Infrastructure.Persistence;

/// <summary>PostgreSQL schemas, one per module boundary. Must match the schemas each module declares in its descriptor.</summary>
public static class Schemas
{
    public const string Content = "content";
    public const string Learning = "learning";
    public const string Srs = "srs";
    public const string Ai = "ai";
    public const string Billing = "billing";
}
