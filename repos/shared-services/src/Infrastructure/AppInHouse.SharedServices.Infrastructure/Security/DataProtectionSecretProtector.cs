using System.Security.Cryptography;
using AppInHouse.SharedServices.Application.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace AppInHouse.SharedServices.Infrastructure.Security;

/// <summary>
/// Protects secrets with the ASP.NET Core Data Protection key ring: authenticated encryption (AES-256-CBC with HMAC-SHA256 by
/// default), keys rotated automatically and kept in the ring, never in the database. The purpose string is part of the key
/// derivation, so text protected for another purpose, or by another application's ring, cannot be opened here.
/// </summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    /// <summary>Change the suffix only together with a migration of every stored secret.</summary>
    public const string Purpose = "AppInHouse.SharedServices.ChannelSecrets.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        return _protector.Protect(plainText);
    }

    public string Unprotect(string protectedText)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedText);
        try
        {
            return _protector.Unprotect(protectedText);
        }
        catch (CryptographicException ex)
        {
            // Never echo the text: it is ciphertext now, but a caller may have passed a plain secret by mistake.
            throw new SecretProtectionException(
                "The secret cannot be decrypted: it was protected with a different key ring, or it was altered.", ex);
        }
    }
}
