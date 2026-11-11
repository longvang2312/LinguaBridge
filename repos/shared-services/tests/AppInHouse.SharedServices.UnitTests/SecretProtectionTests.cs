using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Security;
using AppInHouse.SharedServices.Infrastructure.Security;
using AppInHouse.SharedServices.UnitTests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.UnitTests;

// P0: a secret is never stored or shown in plain text.

public class DataProtectionSecretProtectorTests : IDisposable
{
    private const string Secret = "key-abcdefghijklmnop.0123456789-super-secret";
    private readonly string _ringFolder = Path.Combine(Path.GetTempPath(), "ss-keyring-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_ringFolder)) Directory.Delete(_ringFolder, recursive: true);
    }

    private static DataProtectionSecretProtector With(IDataProtectionProvider provider) => new(provider);

    // The same wiring the hosts use: a key ring in a folder, named by the application name.
    private IDataProtectionProvider RingProvider(string applicationName = "AppInHouse.SharedServices")
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName(applicationName).PersistKeysToFileSystem(new DirectoryInfo(_ringFolder));
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    private DataProtectionSecretProtector WithRing(string applicationName = "AppInHouse.SharedServices") => With(RingProvider(applicationName));

    [Fact]
    public void Protect_ProducesCiphertextThatIsNotTheSecretAndDoesNotContainIt()
    {
        var protector = With(new EphemeralDataProtectionProvider());

        var cipher = protector.Protect(Secret);

        Assert.NotEqual(Secret, cipher);
        Assert.DoesNotContain(Secret, cipher);
        Assert.DoesNotContain("super-secret", cipher);
        Assert.DoesNotContain("key-abc", cipher);
    }

    [Fact]
    public void Unprotect_ReturnsExactlyWhatWasProtected()
    {
        var protector = With(new EphemeralDataProtectionProvider());

        Assert.Equal(Secret, protector.Unprotect(protector.Protect(Secret)));
        Assert.Equal("pässwörd with spaces & «unicode» 🔐", protector.Unprotect(protector.Protect("pässwörd with spaces & «unicode» 🔐")));
    }

    [Fact]
    public void Protect_IsRandomised_SoEqualSecretsDoNotLookEqualInTheDatabase()
    {
        var protector = With(new EphemeralDataProtectionProvider());

        Assert.NotEqual(protector.Protect(Secret), protector.Protect(Secret));
    }

    [Fact]
    public void Unprotect_RejectsAnAlteredCiphertext()
    {
        var protector = With(new EphemeralDataProtectionProvider());
        var cipher = protector.Protect(Secret);
        var altered = cipher[..^2] + (cipher[^2] == 'A' ? 'B' : 'A') + cipher[^1];

        Assert.Throws<SecretProtectionException>(() => protector.Unprotect(altered));
    }

    [Fact]
    public void Unprotect_RejectsPlainTextThatWasNeverProtected_WithoutEchoingIt()
    {
        var protector = With(new EphemeralDataProtectionProvider());

        var exception = Assert.Throws<SecretProtectionException>(() => protector.Unprotect(Secret));

        Assert.DoesNotContain(Secret, exception.Message);
    }

    [Fact]
    public void TheKeyRingOnDisk_LetsAnotherProcessWithTheSameNameReadTheSecret()
    {
        var cipher = WithRing().Protect(Secret);

        // A new provider over the same folder is what a restart (or the web portal) looks like.
        Assert.Equal(Secret, WithRing().Unprotect(cipher));
        Assert.NotEmpty(Directory.GetFiles(_ringFolder, "key-*.xml"));
    }

    [Fact]
    public void AnotherApplicationName_CannotReadTheSecret_EvenWithTheSameKeyFolder()
    {
        var cipher = WithRing("AppInHouse.SharedServices").Protect(Secret);

        Assert.Throws<SecretProtectionException>(() => WithRing("Some.Other.App").Unprotect(cipher));
    }

    [Fact]
    public void AnotherPurpose_CannotReadTheSecret()
    {
        var provider = RingProvider();
        var cipher = With(provider).Protect(Secret);

        // Same key ring, different purpose: what the protector derives its key from.
        Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => provider.CreateProtector("some.other.purpose").Unprotect(cipher));
    }
}

public class SecretMaskingTests
{
    private const string Secret = "my-smtp-P@ssw0rd-1234";

    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void TheMask_IsEightBullets()
    {
        Assert.Equal("••••••••", SecretMask.Value);
        Assert.Equal("••••••••", SecretMask.For(hasSecret: true));
        Assert.Equal(string.Empty, SecretMask.For(hasSecret: false));
        Assert.True(SecretMask.IsMask("••••••••"));
        Assert.False(SecretMask.IsMask("real-secret"));
    }

    [Fact]
    public async Task ADtoSerialisedForTheApi_ShowsOnlyTheMask()
    {
        var fixture = new ManagerFixture();

        var dto = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: Secret));
        var json = JsonSerializer.Serialize(dto, ApiJson);

        Assert.Contains("••••••••", json);
        Assert.Contains("\"hasSecret\":true", json);
        Assert.DoesNotContain(Secret, json);
        Assert.DoesNotContain("P@ssw0rd", json);
        Assert.DoesNotContain(FakeSecretProtector.Prefix, json);   // not even the ciphertext
    }

    [Fact]
    public async Task ADtoWithoutASecret_HasNoMaskToShow()
    {
        var fixture = new ManagerFixture();

        var dto = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: null));

        Assert.False(dto.HasSecret);
        Assert.Equal(string.Empty, dto.MaskedSecret);
    }

    [Fact]
    public void TheDtoType_HasNoPropertyThatCouldHoldASecret()
    {
        var names = typeof(ChannelConfigurationDto).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Equals("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Encrypted", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("MaskedSecret", names);
    }

    [Fact]
    public async Task TheStoredRow_KeepsOnlyTheProtectedSecret()
    {
        var fixture = new ManagerFixture();

        await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: Secret));

        var row = Assert.Single(fixture.Repository.Rows);
        Assert.NotNull(row.EncryptedSecret);
        Assert.DoesNotContain(Secret, row.EncryptedSecret);
        Assert.DoesNotContain(Secret, row.SettingsJson);
        Assert.Equal(Secret, fixture.Protector.Unprotect(row.EncryptedSecret!));
    }

    [Fact]
    public async Task TheAuditLog_NeverContainsTheSecret()
    {
        var fixture = new ManagerFixture();

        var saved = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: Secret));
        await fixture.Manager.ActivateAsync(saved.Id);

        Assert.NotEmpty(fixture.Audit.Entries);
        Assert.All(fixture.Audit.Entries, entry => Assert.DoesNotContain(Secret, entry.DetailsJson));
        Assert.Contains("\"secretChanged\":true", fixture.Audit.Entries[0].DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnlyTheRuntimePath_CanReadTheSecretBack()
    {
        var fixture = new ManagerFixture();
        var saved = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: Secret));
        await fixture.Manager.ActivateAsync(saved.Id);

        var runtime = await fixture.Manager.GetActiveForRuntimeAsync("default", Domain.ChannelType.Email);

        Assert.NotNull(runtime);
        Assert.Equal(Secret, runtime.Secret);
        Assert.DoesNotContain(Secret, runtime.ToString());
    }
}
