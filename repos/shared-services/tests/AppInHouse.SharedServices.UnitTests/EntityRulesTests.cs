using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.UnitTests;

public class ApiKeyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] Scopes = [ApiScopes.EmailSend, ApiScopes.TelegramSend];

    [Fact]
    public void AGeneratedKey_HasTheDocumentedShape()
    {
        var (key, plain) = ApiKey.Generate("linguabridge", "LinguaBridge API", Scopes, null, Now);

        Assert.StartsWith("aih_live_", plain);
        Assert.True(plain.Length >= 32);
        Assert.Equal(ApiKey.PrefixLength, key.KeyPrefix.Length);
        Assert.StartsWith(key.KeyPrefix, plain);
        Assert.Matches("^[a-z0-9]{6}$", key.KeyPrefix[ApiKey.KeyStart.Length..]);
        Assert.Matches("^[0-9a-f]{64}$", key.HashedKey);
    }

    [Fact]
    public void ThePlainKey_IsNotStoredAnywhereOnTheEntity()
    {
        var (key, plain) = ApiKey.Generate("linguabridge", "LinguaBridge API", Scopes, null, Now);
        var secretPart = plain[ApiKey.PrefixLength..];

        var everything = string.Join('|', typeof(ApiKey).GetProperties().Select(p => p.GetValue(key)?.ToString() ?? string.Empty));

        Assert.DoesNotContain(plain, everything);
        Assert.DoesNotContain(secretPart, everything);
        Assert.Equal(ApiKey.Hash(plain), key.HashedKey);
    }

    [Fact]
    public void TheHash_IsSha256OfTheKey()
    {
        // SHA-256("abc") from FIPS 180-2.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ApiKey.Hash("abc"));
    }

    [Fact]
    public void GeneratedKeys_AreUnique()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => ApiKey.Generate("p", "n", Scopes, null, Now).PlainTextKey).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Matches_AcceptsTheRightKey_AndRejectsOthers()
    {
        var (key, plain) = ApiKey.Generate("linguabridge", "n", Scopes, null, Now);

        Assert.True(key.Matches(plain));
        Assert.False(key.Matches(plain + "x"));
        Assert.False(key.Matches(plain[..^1] + (plain[^1] == 'a' ? 'b' : 'a')));
        Assert.False(key.Matches(ApiKey.Generate("linguabridge", "n", Scopes, null, Now).PlainTextKey));
    }

    [Fact]
    public void ARevokedOrExpiredKey_IsNotUsable()
    {
        var (live, _) = ApiKey.Generate("p", "n", Scopes, null, Now);
        var (expiring, _) = ApiKey.Generate("p", "n", Scopes, Now.AddDays(1), Now);
        var (revoked, _) = ApiKey.Generate("p", "n", Scopes, null, Now);
        revoked.Revoke();

        Assert.True(live.IsUsable(Now.AddYears(5)));
        Assert.True(expiring.IsUsable(Now.AddHours(23)));
        Assert.False(expiring.IsUsable(Now.AddDays(1)));
        Assert.False(expiring.IsUsable(Now.AddDays(2)));
        Assert.False(revoked.IsUsable(Now));
    }

    [Fact]
    public void Scopes_AreParsedAndChecked()
    {
        var (key, _) = ApiKey.Generate("p", "n", [" email:send ", "telegram:send", "email:send", ""], null, Now);

        Assert.Equal(["email:send", "telegram:send"], key.ScopeList);
        Assert.True(key.HasScope(ApiScopes.EmailSend));
        Assert.False(key.HasScope(ApiScopes.StorageUpload));
        Assert.False(key.HasScope("email"));   // whole scopes only
    }

    [Fact]
    public void AKeyNeedsAtLeastOneScope_AProject_AndAName()
    {
        Assert.Throws<DomainException>(() => ApiKey.Generate("p", "n", [], null, Now));
        Assert.Throws<DomainException>(() => ApiKey.Generate("p", "n", ["  "], null, Now));
        Assert.Throws<DomainException>(() => ApiKey.Generate("", "n", Scopes, null, Now));
        Assert.Throws<DomainException>(() => ApiKey.Generate("p", " ", Scopes, null, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("aih_live_short")]
    [InlineData("other_live_0123456789012345678901234567890123456789")]   // a key of some other service
    [InlineData("AIH_LIVE_0123456789012345678901234567890123456789")]
    public void ThingsThatAreNotKeys_HaveNoPrefix(string? candidate)
    {
        Assert.False(ApiKey.TryGetPrefix(candidate, out var prefix));
        Assert.Equal(string.Empty, prefix);
    }

    [Fact]
    public void AKeyFromConfiguration_IsHashedLikeAnyOther()
    {
        const string plain = "aih_live_devkey_dev-only-not-a-secret-0123456789";

        var key = ApiKey.FromPlainText("default", "Development key", [ApiScopes.ConfigRead], plain, null, Now);

        Assert.Equal("aih_live_devkey", key.KeyPrefix);
        Assert.True(key.Matches(plain));
        Assert.DoesNotContain(plain, key.HashedKey);
    }

    [Fact]
    public void LastUse_CanBeRecorded()
    {
        var (key, _) = ApiKey.Generate("p", "n", Scopes, null, Now);

        key.MarkUsed(Now.AddMinutes(5));

        Assert.Equal(Now.AddMinutes(5), key.LastUsedAt);
    }
}

public class StoredFileTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private static StoredFile Create(string fileName = "photo.png", string project = "linguabridge") =>
        StoredFile.Create(project, Guid.NewGuid(), ProviderType.LocalDisk, fileName, "image/png", 1234, new string('a', 64), isPublic: false, Now);

    [Fact]
    public void TheObjectKey_IsMadeByTheServer_NotFromTheFileName()
    {
        var file = Create("../../etc/passwd");

        Assert.DoesNotContain("passwd", file.ObjectKey);
        Assert.DoesNotContain("..", file.ObjectKey);
        Assert.Equal($"uploads/linguabridge/2026/11/{file.Id:N}.png", file.ObjectKey);   // uploads/{project}/{yyyy}/{MM}/{guid}{ext}
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\secret.txt", "secret.txt")]
    [InlineData("a/b/c.png", "c.png")]
    public void TheOriginalFileName_IsReducedToAFileName(string sent, string expected) =>
        Assert.Equal(expected, Create(sent).OriginalFileName);

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("audio/mpeg", ".mp3")]
    public void TheExtension_ComesFromTheDetectedType_NotFromTheName(string mime, string extension)
    {
        var file = StoredFile.Create("p", Guid.NewGuid(), ProviderType.LocalDisk, "evil.exe", mime, 1, new string('a', 64), false, Now);

        Assert.EndsWith(extension, file.ObjectKey);
        Assert.DoesNotContain("evil", file.ObjectKey);
    }

    [Fact]
    public void ThePathOfTheKey_FollowsTheUploadDate_InUtc()
    {
        var late = new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.FromHours(-5));   // already 2027-01-01 in UTC
        var file = StoredFile.Create("p", Guid.NewGuid(), ProviderType.LocalDisk, "a.png", "image/png", 1, new string('a', 64), false, late);

        Assert.StartsWith("uploads/p/2027/01/", file.ObjectKey);
    }

    [Fact]
    public void TheThumbnailKey_SitsNextToTheFile_AndKeepsTheFolder()
    {
        var file = Create();

        Assert.Equal($"uploads/linguabridge/2026/11/{file.Id:N}_thumb.webp", file.ThumbnailObjectKey);
        Assert.Equal("a/b.c/x_thumb.webp", StoredFile.ThumbnailKeyFor("a/b.c/x"));   // a dot in a folder name is not an extension
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/gif", false)]
    [InlineData("application/pdf", false)]
    public void OnlyPngJpegAndWebp_CanHaveAThumbnail(string mime, bool expected) =>
        Assert.Equal(expected, StoredFile.Create("p", Guid.NewGuid(), ProviderType.LocalDisk, "a", mime, 1, new string('a', 64), false, Now).CanHaveThumbnail);

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\system32\\evil.dll", "evil.dll")]
    [InlineData("/etc/shadow", "shadow")]
    [InlineData("..", "file")]
    [InlineData("   ", "file")]
    [InlineData("ab.png", "ab.png")]
    public void TheFileName_IsCleanedOfPathsAndControlCharacters(string sent, string expected) =>
        Assert.Equal(expected, StoredFile.SafeFileName(sent));

    [Fact]
    public void ALongFileName_IsCutToTheEnd_WhereTheExtensionIs() =>
        Assert.Equal(255, StoredFile.SafeFileName(new string('x', 400) + ".png").Length);

    [Fact]
    public void TwoFilesWithTheSameName_GetDifferentKeys() =>
        Assert.NotEqual(Create().ObjectKey, Create().ObjectKey);

    [Fact]
    public void OnlyStorageProvidersAreAccepted()
    {
        Assert.Throws<DomainException>(() =>
            StoredFile.Create("p", Guid.NewGuid(), ProviderType.Smtp, "a.png", "image/png", 1, new string('a', 64), false, Now));
        Assert.Equal(ProviderType.Mock, StoredFile.Create("p", Guid.NewGuid(), ProviderType.Mock, "a.png", "image/png", 1, new string('a', 64), false, Now).StorageProvider);
        Assert.Throws<DomainException>(() =>
            StoredFile.Create("p", Guid.NewGuid(), ProviderType.LocalDisk, "a.png", "image/png", -1, new string('a', 64), false, Now));
    }
}

public class LogEntityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFourLogs_AreSeparateTypesWithTheirOwnFields()
    {
        var delivery = DeliveryLog.Create("p", ChannelType.Email, "a@b.c", DeliveryStatus.Sent, "250 OK", 120, Now);
        var media = MediaLog.Create("p", Guid.NewGuid(), "Upload", "Success", null, Now);
        var audit = AuditLog.Create("admin@appinhouse.local", "ActivateVersion", "ChannelConfiguration", "id", """{"version":2}""", Now);
        var system = SystemLog.Create("Error", "Smtp", "connection refused", "System.Net...", Now);

        Assert.Equal((DeliveryStatus.Sent, 120), (delivery.Status, delivery.LatencyMs));
        Assert.Equal("Upload", media.Action);
        Assert.Equal("ActivateVersion", audit.Action);
        Assert.Equal("Error", system.Level);
        Assert.All(new[] { delivery.Id, media.Id, audit.Id, system.Id }, id => Assert.NotEqual(Guid.Empty, id));
    }
}
