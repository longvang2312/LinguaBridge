using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.IntegrationTests;

public class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Root = "/api/v1/channel-configurations";
    private const string Secret = "Sup3r-S3cret-P@ssw0rd!";

    private static object SmtpBody(string name, string? secret = Secret, object? settings = null) => new
    {
        projectCode = "linguabridge",
        channelType = "Email",
        providerType = "Smtp",
        name,
        settings = settings ?? new { host = "smtp.example.com", port = 587 },
        secret,
    };

    private async Task<HttpClient> AdminClientAsync() =>
        factory.ClientWithKey((await factory.CreateKeyAsync(ApiScopes.ConfigRead, ApiScopes.ConfigWrite)).PlainKey);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    // ------------------------------------------------------------------------------------------------ host and authentication

    [Fact]
    public async Task Health_AnswersOk_WithoutAnyCredentials()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal("AppInHouse.SharedServices.Api", body.GetProperty("module").GetString());
    }

    [Fact]
    public async Task TheHost_UsesTheTestDatabase_NotTheDevelopmentOne()
    {
        var connection = await factory.WithDbAsync(db => Task.FromResult(db.Database.GetConnectionString()));

        Assert.Contains(factory.DatabasePath, connection);
        Assert.True(File.Exists(factory.DatabasePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("aih_live_unknwn_0000000000000000000000000000000000000000")]
    public async Task TheConfigurationEndpoints_RefuseAMissingOrUnknownKey(string? key)
    {
        var client = factory.CreateClient();
        if (key is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", key);

        var response = await client.GetAsync(Root);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AKeyWithTheRightPrefixButTheWrongSecret_IsRefused()
    {
        var (plain, _) = await factory.CreateKeyAsync(ApiScopes.ConfigRead);
        var forged = plain[..^3] + (plain.EndsWith("abc", StringComparison.Ordinal) ? "xyz" : "abc");

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(forged).GetAsync(Root)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientWithKey(plain).GetAsync(Root)).StatusCode);
    }

    [Fact]
    public async Task ARevokedKey_StopsWorkingAtOnce()
    {
        var (plain, key) = await factory.CreateKeyAsync(ApiScopes.ConfigRead);
        var client = factory.ClientWithKey(plain);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Root)).StatusCode);

        await factory.WithDbAsync(async db =>
        {
            var row = await db.ApiKeys.SingleAsync(k => k.Id == key.Id);
            row.Revoke();
            return await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Root)).StatusCode);
    }

    [Fact]
    public async Task AnExpiredKey_IsRefused()
    {
        var (plain, _) = await factory.CreateKeyAsync(DateTimeOffset.UtcNow.AddMinutes(-1), ApiScopes.ConfigRead);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(plain).GetAsync(Root)).StatusCode);
    }

    [Fact]
    public async Task ABearerTokenIsNotAccepted_WhenNoIdentityServerIsConfigured()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer some.jwt.token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Root)).StatusCode);
    }

    [Fact]
    public async Task TheKeysLastUse_IsRecorded()
    {
        var (plain, key) = await factory.CreateKeyAsync(ApiScopes.ConfigRead);
        await factory.ClientWithKey(plain).GetAsync(Root);

        var lastUsed = await factory.WithDbAsync(db => db.ApiKeys.Where(k => k.Id == key.Id).Select(k => k.LastUsedAt).SingleAsync());

        Assert.NotNull(lastUsed);
    }

    [Fact]
    public async Task ScopesDecideWhatAKeyMayDo()
    {
        var readOnly = factory.ClientWithKey((await factory.CreateKeyAsync(ApiScopes.ConfigRead)).PlainKey);
        var writeOnly = factory.ClientWithKey((await factory.CreateKeyAsync(ApiScopes.ConfigWrite)).PlainKey);
        var unrelated = factory.ClientWithKey((await factory.CreateKeyAsync(ApiScopes.EmailSend)).PlainKey);
        var body = SmtpBody("Scope test");

        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(Root)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync(Root, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await writeOnly.PostAsJsonAsync(Root, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writeOnly.GetAsync(Root)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unrelated.GetAsync(Root)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unrelated.PostAsJsonAsync($"{Root}/rollback", new { projectCode = "x", channelType = "Email", name = "n" })).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ secrets

    [Fact]
    public async Task ASavedSecret_NeverComesBack_NotEvenInTheResponseToTheCallThatSavedIt()
    {
        var client = await AdminClientAsync();

        var created = await client.PostAsJsonAsync(Root, SmtpBody("Secret hunt"));
        var createdText = await created.Content.ReadAsStringAsync();
        var id = JsonDocument.Parse(createdText).RootElement.GetProperty("id").GetString();
        var fetchedText = await (await client.GetAsync($"{Root}/{id}")).Content.ReadAsStringAsync();
        var listText = await (await client.GetAsync(Root)).Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        foreach (var text in new[] { createdText, fetchedText, listText })
        {
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain("S3cret", text);
            Assert.Contains("••••••••", text);   // readable, not •-escaped
            Assert.Contains("\"hasSecret\":true", text);
        }
    }

    [Fact]
    public async Task TheDatabase_HoldsOnlyCiphertext_ThatTheKeyRingCanOpen()
    {
        var client = await AdminClientAsync();
        var created = await ReadAsync(await client.PostAsJsonAsync(Root, SmtpBody("Ciphertext check")));
        var id = created.GetProperty("id").GetGuid();

        var stored = await factory.WithDbAsync(db => db.ChannelConfigurations.Where(c => c.Id == id).Select(c => c.EncryptedSecret).SingleAsync());
        var raw = ReadSharedText(factory.DatabasePath) + ReadSharedText(factory.DatabasePath + "-wal");

        Assert.NotNull(stored);
        Assert.DoesNotContain(Secret, stored);
        Assert.StartsWith("CfDJ8", stored);   // the Data Protection payload header
        Assert.Equal(Secret, factory.Services.GetRequiredService<ISecretProtector>().Unprotect(stored));
        Assert.Contains("CfDJ8", raw);                  // the file does hold the protected secret...
        Assert.DoesNotContain(Secret, raw);             // ...and nothing readable (the main file and the write-ahead log)
        Assert.DoesNotContain("S3cret", raw);
    }

    // The database is open in the running host, so read the files without asking for exclusive access.
    private static string ReadSharedText(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    [Fact]
    public async Task ASecretTypedIntoTheSettings_IsRefused_AndNothingIsStored()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsJsonAsync(Root, SmtpBody("Leaky", secret: null, settings: new { host = "h", password = "plain-in-settings" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("SettingsJson", text);
        Assert.DoesNotContain("plain-in-settings", text);
        Assert.False(await factory.WithDbAsync(db => db.ChannelConfigurations.AnyAsync(c => c.Name == "Leaky")));
    }

    [Fact]
    public async Task TheAuditLog_NamesTheKey_AndNeverContainsTheSecret()
    {
        var (plain, key) = await factory.CreateKeyAsync(ApiScopes.ConfigRead, ApiScopes.ConfigWrite);
        var client = factory.ClientWithKey(plain);
        var created = await ReadAsync(await client.PostAsJsonAsync(Root, SmtpBody("Audited")));
        await client.PostAsync($"{Root}/{created.GetProperty("id").GetGuid()}/activate", content: null);

        var entries = await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.Actor == "apikey:" + key.KeyPrefix).OrderBy(a => a.Action).ToListAsync());

        Assert.Equal(["ActivateVersion", "CreateConfig"], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.DoesNotContain(Secret, e.DetailsJson ?? string.Empty));
        Assert.All(entries, e => Assert.Equal("ChannelConfiguration", e.ResourceType));
    }

    // ------------------------------------------------------------------------------------------------ versions

    [Fact]
    public async Task TheWholeLifeCycle_DraftSaveActivateRollback_ThroughTheApi()
    {
        var client = await AdminClientAsync();
        var v1 = await ReadAsync(await client.PostAsJsonAsync(Root, SmtpBody("Life cycle")));
        var v2 = await ReadAsync(await client.PostAsJsonAsync(Root, SmtpBody("Life cycle", secret: null, settings: new { host = "smtp.example.com", port = 465 })));
        Assert.Equal((1, "Draft"), (v1.GetProperty("version").GetInt32(), v1.GetProperty("status").GetString()));
        Assert.Equal((2, "Draft"), (v2.GetProperty("version").GetInt32(), v2.GetProperty("status").GetString()));
        Assert.True(v2.GetProperty("hasSecret").GetBoolean());   // version 2 kept the secret

        var activated = await ReadAsync(await client.PostAsync($"{Root}/{v2.GetProperty("id").GetGuid()}/activate", content: null));
        Assert.Equal("Active", activated.GetProperty("status").GetString());
        var afterActivate = await StatusesAsync(client, "Life cycle");
        Assert.Equal(new Dictionary<int, string> { [1] = "Archived", [2] = "Active" }, afterActivate);

        var rolledBack = await ReadAsync(await client.PostAsJsonAsync($"{Root}/rollback",
            new { projectCode = "linguabridge", channelType = "Email", name = "Life cycle", targetVersion = 1 }));
        Assert.Equal(1, rolledBack.GetProperty("version").GetInt32());
        Assert.Equal(new Dictionary<int, string> { [1] = "Active", [2] = "Archived" }, await StatusesAsync(client, "Life cycle"));
    }

    private static async Task<Dictionary<int, string>> StatusesAsync(HttpClient client, string name)
    {
        var list = await ReadAsync(await client.GetAsync($"{Root}?projectCode=linguabridge&channelType=Email"));
        return list.EnumerateArray()
            .Where(c => c.GetProperty("name").GetString() == name)
            .ToDictionary(c => c.GetProperty("version").GetInt32(), c => c.GetProperty("status").GetString()!);
    }

    [Fact]
    public async Task RollbackWithoutATarget_GoesToThePreviouslyActiveVersion_AtOnce()
    {
        var client = await AdminClientAsync();
        var v1 = await ReadAsync(await client.PostAsJsonAsync(Root, SmtpBody("Quick rollback")));
        await client.PostAsync($"{Root}/{v1.GetProperty("id").GetGuid()}/activate", content: null);
        var v2 = await ReadAsync(await client.PostAsJsonAsync(Root, SmtpBody("Quick rollback", secret: null, settings: new { host = "smtp.example.com", port = 25 })));
        await client.PostAsync($"{Root}/{v2.GetProperty("id").GetGuid()}/activate", content: null);

        var rolledBack = await ReadAsync(await client.PostAsJsonAsync($"{Root}/rollback", new { projectCode = "linguabridge", channelType = "Email", name = "Quick rollback" }));

        Assert.Equal(1, rolledBack.GetProperty("version").GetInt32());
        // What a provider reads for its next send: the restored settings and the secret, decrypted in the service only.
        using var scope = factory.Services.CreateScope();
        var runtime = await scope.ServiceProvider.GetRequiredService<IChannelConfigManager>().GetActiveForRuntimeAsync("linguabridge", ChannelType.Email, "Quick rollback");
        Assert.Equal("smtp.example.com", runtime!.Settings.GetProperty("host").GetString());
        Assert.Equal(Secret, runtime.Secret);
    }

    [Fact]
    public async Task ListFilters_ByProjectChannelAndStatus()
    {
        var client = await AdminClientAsync();
        await client.PostAsJsonAsync(Root, new { projectCode = "filterproj", channelType = "Telegram", providerType = "TelegramBot", name = "Bot", settings = new { }, secret = "123456:abcdefghij" });
        await client.PostAsJsonAsync(Root, new { projectCode = "filterproj", channelType = "Storage", providerType = "LocalDisk", name = "Disk", settings = new { rootPath = "data" } });

        var all = await ReadAsync(await client.GetAsync($"{Root}?projectCode=filterproj"));
        var telegram = await ReadAsync(await client.GetAsync($"{Root}?projectCode=filterproj&channelType=Telegram"));
        var active = await ReadAsync(await client.GetAsync($"{Root}?projectCode=filterproj&status=Active"));

        Assert.Equal(2, all.GetArrayLength());
        Assert.Equal("Bot", Assert.Single(telegram.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(0, active.GetArrayLength());
    }

    // ------------------------------------------------------------------------------------------------ errors

    [Fact]
    public async Task InvalidInput_Is400WithFieldErrors()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsJsonAsync(Root, new { projectCode = "Bad Code!", channelType = "Email", providerType = "S3", name = "", settings = new { } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var errors = (await ReadAsync(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("ProjectCode", out _));
        Assert.True(errors.TryGetProperty("ProviderType", out _));
        Assert.True(errors.TryGetProperty("Name", out _));
    }

    [Theory]
    [InlineData("""{"projectCode":"p","channelType":"Fax","providerType":"Smtp","name":"n"}""")]
    [InlineData("""{"projectCode":"p","channelType":"Email","providerType":"Carrier pigeon","name":"n"}""")]
    [InlineData("not json at all")]
    public async Task MalformedBodies_Are400_NotA500(string body)
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsync(Root, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownIds_AndSeries_Are404()
    {
        var client = await AdminClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Root}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{Root}/{Guid.NewGuid()}/activate", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{Root}/rollback", new { projectCode = "nope", channelType = "Email", name = "nope" })).StatusCode);
    }

    [Fact]
    public async Task ActivatingTheActiveVersion_AndRollingBackWithNothingEarlier_Are409()
    {
        var client = await AdminClientAsync();
        var v1 = await ReadAsync(await client.PostAsJsonAsync(Root, SmtpBody("Conflicts")));
        var id = v1.GetProperty("id").GetGuid();
        await client.PostAsync($"{Root}/{id}/activate", content: null);

        var again = await client.PostAsync($"{Root}/{id}/activate", content: null);
        var rollback = await client.PostAsJsonAsync($"{Root}/rollback", new { projectCode = "linguabridge", channelType = "Email", name = "Conflicts" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rollback.StatusCode);
        Assert.Contains("already active", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AStoredSecretThatCannotBeDecrypted_FailsWithoutEchoingWhatIsStored()
    {
        var client = await AdminClientAsync();
        var created = await ReadAsync(await client.PostAsJsonAsync(Root, SmtpBody("Unreadable")));
        var id = created.GetProperty("id").GetGuid();
        await client.PostAsync($"{Root}/{id}/activate", content: null);
        await factory.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE channel_configurations SET encrypted_secret = 'garbage-that-was-never-protected' WHERE id = {0}", id.ToString().ToUpperInvariant());
            return 0;
        });

        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IChannelConfigManager>();
        var exception = await Assert.ThrowsAsync<SecretProtectionException>(
            () => manager.GetActiveForRuntimeAsync("linguabridge", ChannelType.Email, "Unreadable"));

        Assert.DoesNotContain("garbage", exception.Message);
        // The management screens still work: they only ever show the mask.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Root}/{id}")).StatusCode);
    }
}
