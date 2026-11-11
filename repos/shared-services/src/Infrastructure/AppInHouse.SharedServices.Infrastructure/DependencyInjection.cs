using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.Infrastructure;

/// <summary>Where the Data Protection key ring lives. Section <c>DataProtection</c>.</summary>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// The same name in every host that must read the same secrets (the API and, later, the web portal). Changing it makes
    /// every stored secret unreadable.
    /// </summary>
    public string ApplicationName { get; set; } = "AppInHouse.SharedServices";

    /// <summary>
    /// A folder for the key ring; relative paths are resolved against the host's content root. Empty means the framework default
    /// (the user profile), which is fine on a developer machine and wrong in a container: set it, and mount it as a volume.
    /// </summary>
    public string? KeysPath { get; set; }
}

public static class DependencyInjection
{
    /// <param name="contentRootPath">The host's content root, the base of a relative <c>DataProtection:KeysPath</c>.</param>
    public static IServiceCollection AddSharedServicesInfrastructure(
        this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        var options = configuration.GetSection(SecretProtectionOptions.SectionName).Get<SecretProtectionOptions>()
                      ?? new SecretProtectionOptions();

        var builder = services.AddDataProtection().SetApplicationName(options.ApplicationName);
        if (!string.IsNullOrWhiteSpace(options.KeysPath))
        {
            var directory = Directory.CreateDirectory(Path.GetFullPath(options.KeysPath, contentRootPath));
            builder.PersistKeysToFileSystem(directory);

            // Keys written to a folder are plain XML unless something encrypts them. On Windows DPAPI can (bound to the account
            // running the host); elsewhere protect the folder itself (permissions, volume) or add a certificate.
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        }

        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return services;
    }
}
