namespace AppInHouse.SharedServices.Application.Configurations;

/// <summary>
/// What the host allows, which saving a configuration must respect. Filled in by the infrastructure from the host settings;
/// without it the strictest values are used.
/// </summary>
public sealed record ConfigurationRules(bool AllowAbsoluteStoragePaths = false)
{
    public static ConfigurationRules Default { get; } = new();
}
