using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppInHouse.SharedServices.Application.ApiKeys;
using AppInHouse.SharedServices.Application.Audit;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.UnitTests.Support;

namespace AppInHouse.SharedServices.UnitTests;

public class ApiKeyManagerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public FakeApiKeyRepository Repository { get; } = new();
        public FakeAuditLogWriter Audit { get; } = new();
        public FakeUnitOfWork Unit { get; } = new();
        public FixedClock Clock { get; } = new(T0);
        public ApiKeyManager Manager { get; }

        public Fixture() => Manager = new ApiKeyManager(Repository, Audit, Unit, Clock);

        public Task<CreatedApiKeyDto> CreateAsync(string project = "linguabridge", string name = "LinguaBridge API", string[]? scopes = null, int? days = null) =>
            Manager.CreateAsync(new CreateApiKeyCommand(project, name, scopes ?? [ApiScopes.EmailSend], days));
    }

    // ------------------------------------------------------------------------------------------------ what is stored

    [Fact]
    public async Task ANewKey_HasThePrefix_SixRandomCharacters_AndA256BitSecret()
    {
        var created = await new Fixture().CreateAsync();

        var plain = created.PlainTextKey;
        Assert.StartsWith("aih_live_", plain);
        Assert.Matches("^aih_live_[a-z0-9]{6}[A-Za-z0-9_-]{43}$", plain);   // 6 id characters, then 32 random bytes in base64url (43 characters)
        Assert.Equal(plain[..15], created.Key.KeyPrefix);
    }

    [Fact]
    public async Task OnlyTheHashAndThePrefixAreStored_NeverTheKey()
    {
        var f = new Fixture();

        var created = await f.CreateAsync();

        var row = Assert.Single(f.Repository.Rows);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(created.PlainTextKey))), row.HashedKey);
        Assert.Equal(created.PlainTextKey[..15], row.KeyPrefix);
        Assert.Equal(64, row.HashedKey.Length);

        // Nothing the entity holds (any property, any text) contains the key or the part of it after the prefix.
        var everything = string.Join('|', typeof(ApiKey).GetProperties().Select(p => p.GetValue(row)?.ToString() ?? string.Empty));
        Assert.DoesNotContain(created.PlainTextKey, everything);
        Assert.DoesNotContain(created.PlainTextKey[15..], everything);
    }

    [Fact]
    public async Task TheKeyIsInTheAnswerOfCreating_AndNowhereElse()
    {
        var f = new Fixture();
        var created = await f.CreateAsync();

        var listed = await f.Manager.ListAsync(null);
        var fetched = await f.Manager.GetAsync(created.Key.Id);

        foreach (var text in new[] { JsonSerializer.Serialize(created.Key), JsonSerializer.Serialize(listed), JsonSerializer.Serialize(fetched) })
        {
            Assert.DoesNotContain(created.PlainTextKey[15..], text);
            Assert.DoesNotContain(f.Repository.Rows[0].HashedKey, text);   // not the hash either
        }

        Assert.DoesNotContain(created.PlainTextKey[15..], created.ToString());   // a log line that prints the record cannot leak the key
        Assert.DoesNotContain(created.PlainTextKey[15..], string.Join('|', f.Audit.Entries.Select(e => e.DetailsJson)));
        Assert.DoesNotContain(f.Repository.Rows[0].HashedKey, string.Join('|', f.Audit.Entries.Select(e => e.DetailsJson)));
    }

    [Fact]
    public async Task EveryKeyIsDifferent_EvenForTheSameProjectAndName()
    {
        var f = new Fixture();
        var keys = new List<string>();
        for (var i = 0; i < 50; i++) keys.Add((await f.CreateAsync()).PlainTextKey);

        Assert.Equal(50, keys.Distinct().Count());
        Assert.Equal(50, f.Repository.Rows.Select(r => r.HashedKey).Distinct().Count());
    }

    [Fact]
    public async Task AKey_MatchesItself_AndNothingElse()
    {
        var f = new Fixture();
        var a = await f.CreateAsync();
        var b = await f.CreateAsync();
        var row = f.Repository.Rows[0];

        Assert.True(row.Matches(a.PlainTextKey));
        Assert.False(row.Matches(b.PlainTextKey));
        Assert.False(row.Matches(a.PlainTextKey + "x"));
        Assert.False(row.Matches(a.PlainTextKey[..^1]));
        Assert.False(row.Matches(string.Empty));
    }

    [Fact]
    public void TheComparisonOfHashes_IsDoneInConstantTime()
    {
        // The comparison goes through CryptographicOperations.FixedTimeEquals. Checking the source is the only way to see that
        // from the outside: a timing test would be flaky, and a == on the strings would pass every behavioural test.
        var source = File.ReadAllText(FindFile("ApiKey.cs"));

        Assert.Contains("CryptographicOperations.FixedTimeEquals", source);
        Assert.DoesNotContain("HashedKey ==", source);
        Assert.DoesNotContain("== HashedKey", source);
    }

    // ------------------------------------------------------------------------------------------------ validation and expiry

    [Fact]
    public async Task EveryScopeOfTheForm_IsAccepted()
    {
        var created = await new Fixture().CreateAsync(scopes: [.. ApiScopes.Assignable]);

        Assert.Equal(7, created.Key.Scopes.Count);
        Assert.Equal(["config:read", "config:write", "email:send", "chat:send", "storage:upload", "storage:read", "storage:delete"], created.Key.Scopes);
    }

    [Theory]
    [InlineData("nonsense:scope")]
    [InlineData("telegram:send")]   // the older scope is still honoured on keys that have it, but is no longer offered
    [InlineData("*")]
    [InlineData("shared_services_admin")]
    public async Task AnUnknownScope_IsRefused_AndNothingIsStored(string scope)
    {
        var f = new Fixture();

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.CreateAsync(scopes: [ApiScopes.EmailSend, scope]));

        Assert.Contains("Scopes", error.Errors.Keys);
        Assert.Contains(scope, error.Errors["Scopes"][0]);
        Assert.Empty(f.Repository.Rows);
        Assert.Empty(f.Audit.Entries);
    }

    [Fact]
    public async Task NoScope_ABadName_AndABadProject_AreRefused()
    {
        var f = new Fixture();

        Assert.Contains("Scopes", (await Assert.ThrowsAsync<ValidationException>(() => f.CreateAsync(scopes: []))).Errors.Keys);
        Assert.Contains("Scopes", (await Assert.ThrowsAsync<ValidationException>(() => f.CreateAsync(scopes: [" ", ""]))).Errors.Keys);
        Assert.Contains("Name", (await Assert.ThrowsAsync<ValidationException>(() => f.CreateAsync(name: "  "))).Errors.Keys);
        Assert.Contains("Name", (await Assert.ThrowsAsync<ValidationException>(() => f.CreateAsync(name: new string('x', 101)))).Errors.Keys);
        Assert.Contains("Name", (await Assert.ThrowsAsync<ValidationException>(() => f.CreateAsync(name: "two\nlines"))).Errors.Keys);
        Assert.Contains("ProjectCode", (await Assert.ThrowsAsync<ValidationException>(() => f.CreateAsync(project: "Bad Project!"))).Errors.Keys);
        Assert.Contains("ProjectCode", (await Assert.ThrowsAsync<ValidationException>(() => f.CreateAsync(project: ""))).Errors.Keys);
        Assert.Empty(f.Repository.Rows);
    }

    [Fact]
    public async Task TheProject_IsNormalised_AndAScopeIsOnlyListedOnce()
    {
        var created = await new Fixture().CreateAsync(project: "  LinguaBridge ", scopes: [ApiScopes.EmailSend, ApiScopes.EmailSend, " email:send "]);

        Assert.Equal("linguabridge", created.Key.ProjectCode);
        Assert.Equal(["email:send"], created.Key.Scopes);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(365)]
    [InlineData(1)]
    [InlineData(3650)]
    public async Task AKeyWithALifetime_EndsThatManyDaysFromNow(int days)
    {
        var created = await new Fixture().CreateAsync(days: days);

        Assert.Equal(T0.AddDays(days), created.Key.ExpiresAt);
        Assert.Equal(ApiKeyStatus.Active, created.Key.Status);
    }

    [Fact]
    public async Task AKeyWithoutALifetime_NeverExpires()
    {
        var f = new Fixture();
        var created = await f.CreateAsync();

        f.Clock.Advance(TimeSpan.FromDays(365 * 20));

        Assert.Null(created.Key.ExpiresAt);
        Assert.Equal(ApiKeyStatus.Active, (await f.Manager.GetAsync(created.Key.Id))!.Status);
        Assert.True(f.Repository.Rows[0].IsUsable(f.Clock.GetUtcNow()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(3651)]
    public async Task ALifetimeOutsideTheRange_IsRefused(int days)
    {
        var f = new Fixture();

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.CreateAsync(days: days));

        Assert.Contains("ExpiresInDays", error.Errors.Keys);
        Assert.Empty(f.Repository.Rows);
    }

    [Fact]
    public async Task AKeyThatHasExpired_IsNotUsable_AndIsShownAsExpired()
    {
        var f = new Fixture();
        var created = await f.CreateAsync(days: 30);
        var row = f.Repository.Rows[0];

        Assert.True(row.IsUsable(T0.AddDays(29)));
        Assert.False(row.IsUsable(T0.AddDays(30)));   // at the end moment it is over
        Assert.False(row.IsUsable(T0.AddDays(31)));
        f.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(ApiKeyStatus.Expired, (await f.Manager.GetAsync(created.Key.Id))!.Status);
    }

    // ------------------------------------------------------------------------------------------------ revoking

    [Fact]
    public async Task ARevokedKey_IsNotUsable_AtOnce_AndIsShownAsRevoked()
    {
        var f = new Fixture();
        var created = await f.CreateAsync(days: 90);

        var revoked = await f.Manager.RevokeAsync(created.Key.Id);

        Assert.Equal(ApiKeyStatus.Revoked, revoked.Status);
        Assert.False(f.Repository.Rows[0].IsUsable(T0));   // not "after a while": now
        Assert.True(f.Repository.Rows[0].IsRevoked);
    }

    [Fact]
    public async Task ARevokedKeyThatWouldAlsoHaveExpired_IsShownAsRevoked()
    {
        var f = new Fixture();
        var created = await f.CreateAsync(days: 1);
        await f.Manager.RevokeAsync(created.Key.Id);
        f.Clock.Advance(TimeSpan.FromDays(5));

        Assert.Equal(ApiKeyStatus.Revoked, (await f.Manager.GetAsync(created.Key.Id))!.Status);
    }

    [Fact]
    public async Task Revoking_IsAudited_WithThePrefix_AndOnlyOnce()
    {
        var f = new Fixture();
        var created = await f.CreateAsync();
        f.Audit.Entries.Clear();

        await f.Manager.RevokeAsync(created.Key.Id);
        await f.Manager.RevokeAsync(created.Key.Id);   // already revoked: nothing changes

        var entry = Assert.Single(f.Audit.Entries);
        Assert.Equal(("RevokeApiKey", "ApiKey", created.Key.Id.ToString()), (entry.Action, entry.ResourceType, entry.ResourceId));
        Assert.Contains(created.Key.KeyPrefix, entry.DetailsJson);
        Assert.DoesNotContain(created.PlainTextKey[15..], entry.DetailsJson);
    }

    [Fact]
    public async Task RevokingAnUnknownKey_IsNotFound() =>
        await Assert.ThrowsAsync<NotFoundException>(() => new Fixture().Manager.RevokeAsync(Guid.NewGuid()));

    // ------------------------------------------------------------------------------------------------ creating is audited

    [Fact]
    public async Task Creating_IsAudited_WithProjectNamePrefixScopesAndEnd_ButNeverTheKey()
    {
        var f = new Fixture();

        var created = await f.CreateAsync("identity", "Identity prod", [ApiScopes.EmailSend, ApiScopes.ConfigRead], 90);

        var entry = Assert.Single(f.Audit.Entries);
        Assert.Equal(("CreateApiKey", "ApiKey", created.Key.Id.ToString()), (entry.Action, entry.ResourceType, entry.ResourceId));
        using var details = JsonDocument.Parse(entry.DetailsJson);
        Assert.Equal(("identity", "Identity prod", created.Key.KeyPrefix), (details.RootElement.GetProperty("ProjectCode").GetString(), details.RootElement.GetProperty("Name").GetString(), details.RootElement.GetProperty("KeyPrefix").GetString()));
        Assert.Equal(2, details.RootElement.GetProperty("Scopes").GetArrayLength());
        Assert.DoesNotContain(created.PlainTextKey[15..], entry.DetailsJson);
    }

    // ------------------------------------------------------------------------------------------------ rotation

    [Fact]
    public async Task Rotating_MakesANewKeyLikeTheOld_AndBothWorkDuringTheGracePeriod()
    {
        var f = new Fixture();
        var old = await f.CreateAsync("identity", "Identity prod", [ApiScopes.EmailSend, ApiScopes.ChatSend]);

        var rotated = await f.Manager.RotateAsync(old.Key.Id, graceDays: 7);

        Assert.NotEqual(old.PlainTextKey, rotated.NewKey.PlainTextKey);
        Assert.NotEqual(old.Key.Id, rotated.NewKey.Key.Id);
        Assert.Equal(("identity", "Identity prod"), (rotated.NewKey.Key.ProjectCode, rotated.NewKey.Key.Name));
        Assert.Equal(old.Key.Scopes, rotated.NewKey.Key.Scopes);
        Assert.Equal(T0.AddDays(7), rotated.OldKey.ExpiresAt);
        Assert.Equal(2, f.Repository.Rows.Count);   // a project may hold several keys at once

        var (oldRow, newRow) = (f.Repository.Rows[0], f.Repository.Rows[1]);
        Assert.True(oldRow.IsUsable(T0.AddDays(6)) && newRow.IsUsable(T0.AddDays(6)));      // both, inside the grace period
        Assert.False(oldRow.IsUsable(T0.AddDays(7)));                                        // the old one has stopped
        Assert.True(newRow.IsUsable(T0.AddDays(400)));                                       // the new one has not
        Assert.True(newRow.Matches(rotated.NewKey.PlainTextKey) && !newRow.Matches(old.PlainTextKey));
    }

    [Fact]
    public async Task AGracePeriodOfZero_RetiresTheOldKeyAtOnce()
    {
        var f = new Fixture();
        var old = await f.CreateAsync();

        await f.Manager.RotateAsync(old.Key.Id, graceDays: 0);

        Assert.False(f.Repository.Rows[0].IsUsable(T0));
        Assert.True(f.Repository.Rows[1].IsUsable(T0));
    }

    [Fact]
    public async Task TheNewKeyKeepsTheOldKeysEnd_AndTheOldNeverLivesLongerThanItWould()
    {
        var f = new Fixture();
        var old = await f.CreateAsync(days: 3);   // ends in 3 days

        var rotated = await f.Manager.RotateAsync(old.Key.Id, graceDays: 30);   // a longer grace period must not extend it

        Assert.Equal(T0.AddDays(3), rotated.OldKey.ExpiresAt);
        Assert.Equal(T0.AddDays(3), rotated.NewKey.Key.ExpiresAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(91)]
    public async Task ABadGracePeriod_IsRefused(int days)
    {
        var f = new Fixture();
        var old = await f.CreateAsync();

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.Manager.RotateAsync(old.Key.Id, days));

        Assert.Contains("GraceDays", error.Errors.Keys);
        Assert.Single(f.Repository.Rows);
    }

    [Fact]
    public async Task OnlyAKeyThatIsStillValid_CanBeRotated()
    {
        var f = new Fixture();
        var revoked = await f.CreateAsync();
        await f.Manager.RevokeAsync(revoked.Key.Id);
        var expired = await f.CreateAsync(days: 1);
        f.Clock.Advance(TimeSpan.FromDays(2));

        await Assert.ThrowsAsync<ValidationException>(() => f.Manager.RotateAsync(revoked.Key.Id, 7));
        await Assert.ThrowsAsync<ValidationException>(() => f.Manager.RotateAsync(expired.Key.Id, 7));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Manager.RotateAsync(Guid.NewGuid(), 7));
        Assert.Equal(2, f.Repository.Rows.Count);
    }

    [Fact]
    public async Task Rotating_IsAudited_WithBothPrefixes_ButNeitherKey()
    {
        var f = new Fixture();
        var old = await f.CreateAsync();
        f.Audit.Entries.Clear();

        var rotated = await f.Manager.RotateAsync(old.Key.Id, 7);

        var entry = Assert.Single(f.Audit.Entries);
        Assert.Equal("RotateApiKey", entry.Action);
        Assert.Contains(old.Key.KeyPrefix, entry.DetailsJson);
        Assert.Contains(rotated.NewKey.Key.KeyPrefix, entry.DetailsJson);
        Assert.DoesNotContain(old.PlainTextKey[15..], entry.DetailsJson);
        Assert.DoesNotContain(rotated.NewKey.PlainTextKey[15..], entry.DetailsJson);
    }

    // ------------------------------------------------------------------------------------------------ listing

    [Fact]
    public async Task TheList_IsNewestFirst_PerProject_WithEachStatus()
    {
        var f = new Fixture();
        var a = await f.CreateAsync("a", "first");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var b = await f.CreateAsync("b", "second", days: 1);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var c = await f.CreateAsync("a", "third");
        await f.Manager.RevokeAsync(c.Key.Id);
        f.Clock.Advance(TimeSpan.FromDays(2));   // b has ended now

        var all = await f.Manager.ListAsync(null);
        var onlyA = await f.Manager.ListAsync("A");

        Assert.Equal(["third", "second", "first"], all.Select(k => k.Name));
        Assert.Equal([ApiKeyStatus.Revoked, ApiKeyStatus.Expired, ApiKeyStatus.Active], all.Select(k => k.Status));
        Assert.Equal(["third", "first"], onlyA.Select(k => k.Name));
        Assert.Empty(await f.Manager.ListAsync("nobody"));
        Assert.Equal(T0, a.Key.CreatedAt);
        Assert.Equal(b.Key.Id, all[1].Id);
    }

    [Fact]
    public async Task TheLastUseIsShown_WhenTheKeyHasBeenUsed()
    {
        var f = new Fixture();
        var created = await f.CreateAsync();
        Assert.Null((await f.Manager.GetAsync(created.Key.Id))!.LastUsedAt);

        f.Repository.Rows[0].MarkUsed(T0.AddMinutes(5));

        Assert.Equal(T0.AddMinutes(5), (await f.Manager.GetAsync(created.Key.Id))!.LastUsedAt);
    }

    [Fact]
    public void ShortenExpiry_OnlyEverMakesTheEndEarlier()
    {
        var (key, _) = ApiKey.Generate("p", "n", [ApiScopes.EmailSend], T0.AddDays(10), T0);

        key.ShortenExpiry(T0.AddDays(20));
        Assert.Equal(T0.AddDays(10), key.ExpiresAt);
        key.ShortenExpiry(T0.AddDays(2));
        Assert.Equal(T0.AddDays(2), key.ExpiresAt);

        var (forever, _) = ApiKey.Generate("p", "n", [ApiScopes.EmailSend], null, T0);
        forever.ShortenExpiry(T0.AddDays(5));
        Assert.Equal(T0.AddDays(5), forever.ExpiresAt);
    }

    private static string FindFile(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var found = Directory.EnumerateFiles(Path.Combine(directory.FullName), name, SearchOption.AllDirectories)
                .FirstOrDefault(p => p.Contains("AppInHouse.SharedServices.Domain") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
            if (found is not null) return found;
        }

        throw new FileNotFoundException($"{name} not found above {AppContext.BaseDirectory}");
    }
}

public class AuditLogReaderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private static AuditLog Entry(string action, string? details, int minutes = 0, string actor = "apikey:aih_live_abc123") =>
        AuditLog.Create(actor, action, "ApiKey", Guid.NewGuid().ToString(), details, T0.AddMinutes(minutes));

    private static (AuditLogReader Reader, FakeAuditLogRepository Repository) Make(params AuditLog[] rows)
    {
        var repository = new FakeAuditLogRepository();
        repository.Rows.AddRange(rows);
        return (new AuditLogReader(repository), repository);
    }

    [Fact]
    public async Task TheLog_IsNewestFirst_WithEveryField()
    {
        var (reader, _) = Make(Entry("CreateConfig", """{"name":"x"}""", 0), Entry("SendTest", null, 5, actor: "long@example.com"));

        var rows = await reader.ListAsync(new AuditLogQuery());

        Assert.Equal(["SendTest", "CreateConfig"], rows.Select(r => r.Action));
        Assert.Equal(("long@example.com", "ApiKey", T0.AddMinutes(5)), (rows[0].Actor, rows[0].ResourceType, rows[0].CreatedAt));
        Assert.Null(rows[0].DetailsJson);
        Assert.Contains("\"name\":\"x\"", rows[1].DetailsJson);
    }

    [Fact]
    public async Task TheFilters_ArePassedOn_TrimmedAndEmptyMeansAny()
    {
        var (reader, repository) = Make();

        await reader.ListAsync(new AuditLogQuery("  long  ", " SendTest ", T0, T0.AddDays(1), 25, 50));
        Assert.Equal(("long", "SendTest", T0, T0.AddDays(1), 25, 50), repository.LastQuery);

        await reader.ListAsync(new AuditLogQuery("  ", "", null, null, 10, 0));
        Assert.Equal((null, null, null, null, 10, 0), repository.LastQuery);
    }

    [Fact]
    public async Task TheFiltersSelect_ByActor_Action_AndPeriod()
    {
        var (reader, _) = Make(
            Entry("SendTest", null, 0, "long@example.com"), Entry("CreateApiKey", null, 60, "apikey:aih_live_abc123"),
            Entry("SendTest", null, 120, "apikey:aih_live_abc123"), Entry("SendTest", null, 180, "long@example.com"));

        Assert.Equal(3, (await reader.ListAsync(new AuditLogQuery(Action: "SendTest"))).Count);
        Assert.Equal(2, (await reader.ListAsync(new AuditLogQuery(Actor: "apikey:"))).Count);
        Assert.Equal(2, (await reader.ListAsync(new AuditLogQuery(From: T0.AddMinutes(60), To: T0.AddMinutes(180)))).Count);   // the end is exclusive
        Assert.Single(await reader.ListAsync(new AuditLogQuery(Actor: "long", Action: "SendTest", From: T0.AddMinutes(100))));
    }

    [Fact]
    public async Task APeriodThatEndsBeforeItStarts_IsRefused() =>
        Assert.Contains("To", (await Assert.ThrowsAsync<ValidationException>(() => Make().Reader.ListAsync(new AuditLogQuery(From: T0, To: T0)))).Errors.Keys);

    [Fact]
    public async Task TheLimit_IsAtLeastOneAndAtMostTwoHundred_AndANegativeOffsetIsZero()
    {
        var (reader, repository) = Make();

        await reader.ListAsync(new AuditLogQuery(Limit: 0, Offset: -4));
        Assert.Equal((1, 0), (repository.LastQuery!.Value.Limit, repository.LastQuery.Value.Offset));
        await reader.ListAsync(new AuditLogQuery(Limit: 100000));
        Assert.Equal(200, repository.LastQuery!.Value.Limit);
    }

    [Fact]
    public async Task TheActionsForTheFilter_AreDistinctAndSorted()
    {
        var (reader, _) = Make(Entry("SendTest", null), Entry("CreateApiKey", null), Entry("SendTest", null));

        Assert.Equal(["CreateApiKey", "SendTest"], await reader.ActionsAsync());
    }

    [Fact]
    public async Task TheDetails_AreCleanedOnTheWayOut_EvenIfSomethingGotIntoThem()
    {
        var token = "123456" + ":" + "fake-token-for-tests-0123456789";
        var (reader, _) = Make(Entry("Odd", $$"""{"Name":"x","Password":"hunter2","note":"{{token}}"}"""));

        var details = (await reader.ListAsync(new AuditLogQuery()))[0].DetailsJson!;

        Assert.DoesNotContain("hunter2", details);
        Assert.DoesNotContain(token, details);
        Assert.Contains("\"Name\":\"x\"", details);
    }
}

public class AuditDetailsTests
{
    [Fact]
    public void ThePropertiesThatNameASecret_AreHidden_WhateverTheirValue()
    {
        var cleaned = AuditDetails.Sanitize("""{"password":"hunter2","SecretAccessKey":"abc","apiKey":"k","Authorization":"Bearer x","token":12345,"name":"keep"}""")!;

        foreach (var leaked in new[] { "hunter2", "\"abc\"", "\"k\"", "Bearer", "12345" }) Assert.DoesNotContain(leaked, cleaned);
        Assert.Contains("\"name\":\"keep\"", cleaned);
        Assert.Contains(AuditDetails.Hidden, cleaned);
    }

    [Fact]
    public void AYesNoFlagAboutASecret_IsKept_BecauseItIsNotTheSecret()
    {
        var cleaned = AuditDetails.Sanitize("""{"SecretChanged":true,"Version":2}""")!;

        Assert.Contains("\"SecretChanged\":true", cleaned);
    }

    [Fact]
    public void ATextThatLooksLikeAKeyAToken_OrAWebhook_IsHidden_WhereverItIs()
    {
        var plainKey = "aih_live_abc123" + "Zk3jT9xQ2mV8nB5cR7wL1pD4sF6hG0yU";
        var token = "123456" + ":" + "fake-token-for-tests-0123456789";
        var discord = "https://discord.com/api/webhooks/" + "123456789012345678" + "/" + "fake-webhook-token-for-tests-0123456789";
        var slack = "https://hooks.slack.com/services/" + "T0FAKE000" + "/" + "B0FAKE000" + "/" + "fakewebhooktokenfortests0123";
        var json = JsonSerializer.Serialize(new { a = plainKey, nested = new { b = token }, list = new[] { discord, slack }, text = $"called {discord} twice" });

        var cleaned = AuditDetails.Sanitize(json)!;

        foreach (var secret in new[] { plainKey, token, discord, slack, "fake-webhook-token-for-tests", "fakewebhooktokenfortests" }) Assert.DoesNotContain(secret, cleaned);
    }

    [Fact]
    public void AKeyPrefix_IsNotASecret_AndStaysVisible()
    {
        var cleaned = AuditDetails.Sanitize("""{"KeyPrefix":"aih_live_abc123","OldKeyPrefix":"aih_live_old999"}""")!;

        Assert.Contains("aih_live_abc123", cleaned);
        Assert.Contains("aih_live_old999", cleaned);
    }

    [Fact]
    public void NothingInNothingOut_AndTextThatIsNotJsonIsCleanedAsText()
    {
        Assert.Null(AuditDetails.Sanitize(null));
        Assert.Null(AuditDetails.Sanitize("  "));
        var token = "123456" + ":" + "fake-token-for-tests-0123456789";
        Assert.DoesNotContain(token, AuditDetails.Sanitize($"not json {token}")!);
    }

    [Fact]
    public void NumbersBooleansNullsAndArrays_SurviveCleaning()
    {
        var cleaned = AuditDetails.Sanitize("""{"Version":3,"Ok":false,"Nothing":null,"Scopes":["a","b"]}""")!;

        using var document = JsonDocument.Parse(cleaned);
        Assert.Equal(3, document.RootElement.GetProperty("Version").GetInt32());
        Assert.False(document.RootElement.GetProperty("Ok").GetBoolean());
        Assert.Equal(2, document.RootElement.GetProperty("Scopes").GetArrayLength());
    }
}
