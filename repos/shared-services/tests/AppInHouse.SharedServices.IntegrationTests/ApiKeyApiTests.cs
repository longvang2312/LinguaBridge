using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.IntegrationTests;

public class ApiKeyApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Keys = "/api/v1/api-keys";
    private const string Audit = "/api/v1/audit-logs";

    private static string NewProject() => "p" + Guid.NewGuid().ToString("N")[..8];
    private static string NewName() => "key-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<HttpClient> AdminAsync() =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite)).PlainKey);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static async Task<(JsonElement Key, string Plain)> CreateAsync(HttpClient admin, string project, string name, string[]? scopes = null, int? days = null)
    {
        var response = await admin.PostAsJsonAsync(Keys, new { projectCode = project, name, scopes = scopes ?? ["config:read", "email:send"], expiresInDays = days });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        return (body.GetProperty("key"), body.GetProperty("apiKey").GetString()!);
    }

    private HttpStatusCode Status(string plainKey, string url = Keys) =>
        factory.ClientWithKey(plainKey).GetAsync(url).GetAwaiter().GetResult().StatusCode;

    // ------------------------------------------------------------------------------------------------ the key is shown once, stored as a hash

    [Fact]
    public async Task ACreatedKey_IsInTheAnswerOnce_WithTheRightShape_AndWorksAtOnce()
    {
        var admin = await AdminAsync();
        var project = NewProject();

        var response = await admin.PostAsJsonAsync(Keys, new { projectCode = project, name = NewName(), scopes = new[] { "config:read", "email:send" }, expiresInDays = 90 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);   // the answer holds the only copy of the key: nothing may cache it
        var body = await ReadAsync(response);
        var plain = body.GetProperty("apiKey").GetString()!;
        Assert.Matches("^aih_live_[a-z0-9]{6}[A-Za-z0-9_-]{43}$", plain);
        var key = body.GetProperty("key");
        Assert.Equal((project, "Active", plain[..15]), (key.GetProperty("projectCode").GetString(), key.GetProperty("status").GetString(), key.GetProperty("keyPrefix").GetString()));
        Assert.Equal($"{Keys}/{key.GetProperty("id").GetGuid()}", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, Status(plain, $"{Keys}?projectCode={project}"));   // it authenticates, with its scopes
        Assert.DoesNotContain(plain[15..], key.GetRawText());   // the key part of the answer holds no secret
    }

    [Fact]
    public async Task TheDatabase_HoldsOnlyTheHashAndThePrefix_NeverTheKey()
    {
        var admin = await AdminAsync();
        var name = NewName();
        var (key, plain) = await CreateAsync(admin, NewProject(), name);

        var row = await factory.WithDbAsync(db => db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == key.GetProperty("id").GetGuid()));
        var dump = await factory.WithDbAsync(db => db.Database
            .SqlQueryRaw<string>("SELECT id || '|' || project_code || '|' || name || '|' || key_prefix || '|' || hashed_key || '|' || scopes AS \"Value\" FROM api_keys WHERE name = {0}", name).SingleAsync());

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plain))), row.HashedKey);
        Assert.Equal(plain[..15], row.KeyPrefix);
        Assert.DoesNotContain(plain[15..], dump);
        Assert.DoesNotContain(plain, dump);
    }

    [Fact]
    public async Task TheListAndTheGet_NeverShowTheKeyOrItsHash()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var (key, plain) = await CreateAsync(admin, project, NewName());
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plain)));

        var listed = await (await admin.GetAsync($"{Keys}?projectCode={project}")).Content.ReadAsStringAsync();

        Assert.Contains(plain[..15], listed);   // the prefix identifies the key
        Assert.DoesNotContain(plain[15..], listed);
        Assert.DoesNotContain(hash, listed);
        Assert.DoesNotContain("hashedKey", listed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(key.GetProperty("id").GetGuid(), JsonDocument.Parse(listed).RootElement[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AKeyWithALifetime_EndsThatManyDaysFromNow_AndOneWithoutDoesNotEnd()
    {
        var admin = await AdminAsync();

        var (limited, _) = await CreateAsync(admin, NewProject(), NewName(), days: 30);
        var (forever, _) = await CreateAsync(admin, NewProject(), NewName());

        var ends = limited.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange((ends - DateTimeOffset.UtcNow).TotalDays, 29.99, 30.01);
        Assert.Equal(JsonValueKind.Null, forever.GetProperty("expiresAt").ValueKind);
    }

    // ------------------------------------------------------------------------------------------------ expired, revoked, last use

    [Fact]
    public async Task AnExpiredKey_Is401_AndAKeyThatStillHasTimeIsNot()
    {
        var (expired, _) = await factory.CreateKeyAsync(DateTimeOffset.UtcNow.AddMinutes(-1), ApiScopes.ConfigRead);
        var (valid, _) = await factory.CreateKeyAsync(DateTimeOffset.UtcNow.AddMinutes(5), ApiScopes.ConfigRead);

        Assert.Equal(HttpStatusCode.Unauthorized, Status(expired));
        Assert.Equal(HttpStatusCode.OK, Status(valid));
    }

    [Fact]
    public async Task ARevokedKey_Is401_AtOnce_AndShowsAsRevoked()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var (key, plain) = await CreateAsync(admin, project, NewName());
        Assert.Equal(HttpStatusCode.OK, Status(plain, $"{Keys}?projectCode={project}"));

        var revoked = await admin.PostAsync($"{Keys}/{key.GetProperty("id").GetGuid()}/revoke", null);

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal("Revoked", (await ReadAsync(revoked)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, Status(plain, $"{Keys}?projectCode={project}"));   // the very next call
        Assert.Equal("Revoked", (await ReadAsync(await admin.GetAsync($"{Keys}?projectCode={project}")))[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Keys}/{key.GetProperty("id").GetGuid()}/revoke", null)).StatusCode);   // again: nothing changes
    }

    [Fact]
    public async Task RevokingOneKey_LeavesTheOthersOfTheProjectWorking()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var (first, firstPlain) = await CreateAsync(admin, project, NewName());
        var (_, secondPlain) = await CreateAsync(admin, project, NewName());

        await admin.PostAsync($"{Keys}/{first.GetProperty("id").GetGuid()}/revoke", null);

        Assert.Equal(HttpStatusCode.Unauthorized, Status(firstPlain));
        Assert.Equal(HttpStatusCode.OK, Status(secondPlain, $"{Keys}?projectCode={project}"));
    }

    [Fact]
    public async Task AWrongOrMalformedKey_Is401_WithoutSayingWhichPartWasWrong()
    {
        var admin = await AdminAsync();
        var (_, plain) = await CreateAsync(admin, NewProject(), NewName());
        var wrongSecret = plain[..15] + new string('x', plain.Length - 15);   // the right prefix, the wrong key

        var a = await factory.ClientWithKey(wrongSecret).GetAsync(Keys);
        var b = await factory.ClientWithKey("aih_live_nosuchkey" + new string('z', 30)).GetAsync(Keys);
        var c = await factory.ClientWithKey("not-a-key").GetAsync(Keys);

        Assert.All(new[] { a, b, c }, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Equal(await a.Content.ReadAsStringAsync(), await b.Content.ReadAsStringAsync());   // the answers do not tell the cases apart
    }

    [Fact]
    public async Task TheLastUse_IsRecordedOnTheFirstCall_AndNotWrittenAgainWithinTheMinute()
    {
        var (plain, key) = await factory.CreateKeyAsync(ApiScopes.ConfigRead);
        Assert.Null((await factory.WithDbAsync(db => db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == key.Id))).LastUsedAt);

        await factory.ClientWithKey(plain).GetAsync(Keys);
        var first = (await factory.WithDbAsync(db => db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == key.Id))).LastUsedAt;
        await Task.Delay(1200);
        await factory.ClientWithKey(plain).GetAsync(Keys);
        var second = (await factory.WithDbAsync(db => db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == key.Id))).LastUsedAt;

        Assert.NotNull(first);
        Assert.Equal(first, second);   // throttled: a busy client does not cause a write per request
        Assert.InRange((DateTimeOffset.UtcNow - first!.Value).TotalSeconds, -1, 30);
    }

    // ------------------------------------------------------------------------------------------------ rotation

    [Fact]
    public async Task Rotating_GivesANewKey_AndBothWorkDuringTheGracePeriod()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var (old, oldPlain) = await CreateAsync(admin, project, "Rotating", ["config:read", "chat:send"]);

        var response = await admin.PostAsync($"{Keys}/{old.GetProperty("id").GetGuid()}/rotate?graceDays=7", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await ReadAsync(response);
        var newPlain = body.GetProperty("apiKey").GetString()!;
        Assert.NotEqual(oldPlain, newPlain);
        Assert.Equal(["config:read", "chat:send"], body.GetProperty("key").GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.InRange((body.GetProperty("oldKey").GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalDays, 6.99, 7.01);
        Assert.Equal(HttpStatusCode.OK, Status(oldPlain, $"{Keys}?projectCode={project}"));    // both are valid in between
        Assert.Equal(HttpStatusCode.OK, Status(newPlain, $"{Keys}?projectCode={project}"));
        Assert.Equal(2, (await ReadAsync(await admin.GetAsync($"{Keys}?projectCode={project}"))).GetArrayLength());   // a project holds several keys
    }

    [Fact]
    public async Task AGracePeriodOfZero_StopsTheOldKeyAtOnce_AndTheNewOneKeepsWorking()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var (old, oldPlain) = await CreateAsync(admin, project, NewName());

        var body = await ReadAsync(await admin.PostAsync($"{Keys}/{old.GetProperty("id").GetGuid()}/rotate?graceDays=0", null));

        Assert.Equal(HttpStatusCode.Unauthorized, Status(oldPlain));
        Assert.Equal(HttpStatusCode.OK, Status(body.GetProperty("apiKey").GetString()!, $"{Keys}?projectCode={project}"));
    }

    [Fact]
    public async Task TheGraceEnds_WhenItsTimeComes_NotBefore()
    {
        var admin = await AdminAsync();
        var (old, oldPlain) = await CreateAsync(admin, NewProject(), NewName());
        await admin.PostAsync($"{Keys}/{old.GetProperty("id").GetGuid()}/rotate?graceDays=7", null);
        var id = old.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, Status(oldPlain));
        // Move the end of the grace period into the past, as a week going by would.
        await factory.WithDbAsync(async db =>
        {
            var row = await db.ApiKeys.SingleAsync(k => k.Id == id);
            typeof(ApiKey).GetProperty(nameof(ApiKey.ExpiresAt))!.SetValue(row, DateTimeOffset.UtcNow.AddMinutes(-1));
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(HttpStatusCode.Unauthorized, Status(oldPlain));
    }

    [Fact]
    public async Task OnlyAKeyThatIsStillValid_CanBeRotated()
    {
        var admin = await AdminAsync();
        var (key, _) = await CreateAsync(admin, NewProject(), NewName());
        var id = key.GetProperty("id").GetGuid();
        await admin.PostAsync($"{Keys}/{id}/revoke", null);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{Keys}/{id}/rotate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{Keys}/{Guid.NewGuid()}/rotate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{Keys}/{Guid.NewGuid()}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{Keys}/{id}/rotate?graceDays=500", null)).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ validation

    [Theory]
    [InlineData("nonsense:scope")]
    [InlineData("telegram:send")]
    [InlineData("shared_services_admin")]
    public async Task AnUnknownScope_Is400_AndNoKeyIsMade(string scope)
    {
        var admin = await AdminAsync();
        var name = NewName();

        var response = await admin.PostAsJsonAsync(Keys, new { projectCode = NewProject(), name, scopes = new[] { "email:send", scope } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(scope, await response.Content.ReadAsStringAsync());
        Assert.False(await factory.WithDbAsync(db => db.ApiKeys.AnyAsync(k => k.Name == name)));
    }

    [Fact]
    public async Task BadInput_Is400WithFieldErrors()
    {
        var admin = await AdminAsync();

        async Task<string[]> ErrorsOf(object body) =>
            (await ReadAsync(await admin.PostAsJsonAsync(Keys, body))).GetProperty("errors").EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Contains("Scopes", await ErrorsOf(new { projectCode = "p1", name = "n", scopes = Array.Empty<string>() }));
        Assert.Contains("Name", await ErrorsOf(new { projectCode = "p1", name = " ", scopes = new[] { "email:send" } }));
        Assert.Contains("ProjectCode", await ErrorsOf(new { projectCode = "Bad Project", name = "n", scopes = new[] { "email:send" } }));
        Assert.Contains("ExpiresInDays", await ErrorsOf(new { projectCode = "p1", name = "n", scopes = new[] { "email:send" }, expiresInDays = 0 }));
        Assert.Contains("ExpiresInDays", await ErrorsOf(new { projectCode = "p1", name = "n", scopes = new[] { "email:send" }, expiresInDays = 4000 }));
    }

    // ------------------------------------------------------------------------------------------------ who may do what

    [Fact]
    public async Task WithoutAKey_401_WithoutTheScope_403()
    {
        var readOnly = factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigRead)).PlainKey);
        var body = new { projectCode = "p1", name = "n", scopes = new[] { "email:send" } };

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).GetAsync(Keys)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).PostAsJsonAsync(Keys, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync(Keys, body)).StatusCode);   // reading keys is not creating them
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(Keys)).StatusCode);
        var writeOnly = factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigWrite)).PlainKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await writeOnly.GetAsync(Keys)).StatusCode);
    }

    [Fact]
    public async Task AProjectKey_ListsItsOwnKeysOnly_AndCannotCreateRevokeOrRotateAny()
    {
        var admin = await AdminAsync();
        var mine = NewProject();
        var other = NewProject();
        var (ownKey, _) = await CreateAsync(admin, mine, "mine", ["config:read", "config:write"]);
        await CreateAsync(admin, other, "theirs");
        var plain = (await factory.CreateKeyForProjectAsync(mine, null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite)).PlainKey;
        var client = factory.ClientWithKey(plain);
        var id = ownKey.GetProperty("id").GetGuid();

        var own = await ReadAsync(await client.GetAsync(Keys));   // no project named: its own, not everyone's
        Assert.All(own.EnumerateArray(), k => Assert.Equal(mine, k.GetProperty("projectCode").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Keys}?projectCode={other}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Keys, new { projectCode = mine, name = "escalate", scopes = new[] { "config:write", "storage:delete" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Keys}/{id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Keys}/{id}/rotate", null)).StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.ApiKeys.AnyAsync(k => k.Name == "escalate")));
        Assert.False((await factory.WithDbAsync(db => db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == id))).IsRevoked);
    }

    [Fact]
    public async Task ThePlatformKey_ListsEveryProject()
    {
        var admin = await AdminAsync();
        var a = NewProject();
        var b = NewProject();
        await CreateAsync(admin, a, NewName());
        await CreateAsync(admin, b, NewName());

        var all = (await ReadAsync(await admin.GetAsync(Keys))).EnumerateArray().Select(k => k.GetProperty("projectCode").GetString()).ToList();

        Assert.Contains(a, all);
        Assert.Contains(b, all);
    }

    // ------------------------------------------------------------------------------------------------ the audit log

    [Fact]
    public async Task CreatingRotatingAndRevoking_AreAudited_WithThePrefix_AndNeverTheKey()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var (key, plain) = await CreateAsync(admin, project, NewName(), days: 30);
        var id = key.GetProperty("id").GetGuid();
        var rotated = await ReadAsync(await admin.PostAsync($"{Keys}/{id}/rotate?graceDays=3", null));
        await admin.PostAsync($"{Keys}/{id}/revoke", null);

        var text = await (await admin.GetAsync($"{Audit}?limit=200")).Content.ReadAsStringAsync();
        var mine = JsonDocument.Parse(text).RootElement.EnumerateArray().Where(l => l.GetProperty("resourceId").GetString() == id.ToString()).ToList();

        Assert.Equal(["CreateApiKey", "RevokeApiKey", "RotateApiKey"], mine.Select(l => l.GetProperty("action").GetString()).Order());
        Assert.All(mine, l => Assert.Equal("ApiKey", l.GetProperty("resourceType").GetString()));
        Assert.All(mine, l => Assert.StartsWith("apikey:aih_live_", l.GetProperty("actor").GetString()));   // who did it: the key's prefix
        Assert.Contains(plain[..15], text);
        Assert.DoesNotContain(plain[15..], text);
        Assert.DoesNotContain(rotated.GetProperty("apiKey").GetString()![15..], text);
    }

    [Fact]
    public async Task TheAuditLog_CanBeFilteredByActionActorAndPeriod_NewestFirst()
    {
        var admin = await AdminAsync();
        var (key, _) = await CreateAsync(admin, NewProject(), NewName());
        await admin.PostAsync($"{Keys}/{key.GetProperty("id").GetGuid()}/revoke", null);
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        var after = DateTimeOffset.UtcNow.AddMinutes(5);

        var revokes = await ReadAsync(await admin.GetAsync($"{Audit}?action=RevokeApiKey&limit=200"));
        var actorFilter = await ReadAsync(await admin.GetAsync($"{Audit}?actor=apikey:aih_live_&limit=200"));
        var period = await ReadAsync(await admin.GetAsync($"{Audit}?from={Uri.EscapeDataString(before.ToString("O"))}&to={Uri.EscapeDataString(after.ToString("O"))}&limit=200"));
        var future = await ReadAsync(await admin.GetAsync($"{Audit}?from={Uri.EscapeDataString(after.ToString("O"))}"));
        var all = await ReadAsync(await admin.GetAsync($"{Audit}?limit=200"));

        Assert.All(revokes.EnumerateArray(), l => Assert.Equal("RevokeApiKey", l.GetProperty("action").GetString()));
        Assert.Contains(revokes.EnumerateArray(), l => l.GetProperty("resourceId").GetString() == key.GetProperty("id").GetGuid().ToString());
        Assert.All(actorFilter.EnumerateArray(), l => Assert.Contains("apikey:aih_live_", l.GetProperty("actor").GetString()));
        Assert.NotEmpty(period.EnumerateArray());
        Assert.Empty(future.EnumerateArray());
        var times = all.EnumerateArray().Select(l => l.GetProperty("createdAt").GetDateTimeOffset()).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);
        Assert.Contains("CreateApiKey", (await ReadAsync(await admin.GetAsync($"{Audit}/actions"))).EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task AnInvertedPeriod_Is400()
    {
        var admin = await AdminAsync();
        var from = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"{Audit}?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(from.AddDays(-1).ToString("O"))}")).StatusCode);
    }

    [Fact]
    public async Task TheAuditLog_IsForThePlatformOnly()
    {
        var project = factory.ClientWithKey((await factory.CreateKeyForProjectAsync(NewProject(), null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite)).PlainKey);

        Assert.Equal(HttpStatusCode.Forbidden, (await project.GetAsync(Audit)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await project.GetAsync($"{Audit}/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).GetAsync(Audit)).StatusCode);
    }

    [Fact]
    public async Task ASecretSavedInAConfiguration_NeverReachesTheAuditLogAsItIsShown()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        const string secret = "Audit-test-SMTP-P@ssw0rd-777";
        await admin.PostAsJsonAsync("/api/v1/channel-configurations", new
        {
            projectCode = project, channelType = "Email", providerType = "Smtp", name = "Mail", settings = new { host = "smtp.example.com", port = 587, username = "u" }, secret,
        });

        var text = await (await admin.GetAsync($"{Audit}?limit=200")).Content.ReadAsStringAsync();

        Assert.Contains(project, text);
        Assert.DoesNotContain(secret, text);
        Assert.Matches(new Regex("\"SecretChanged\":true|\\\\\"secretChanged\\\\\":true", RegexOptions.IgnoreCase), text);   // the flag is kept, the secret is not
    }
}
