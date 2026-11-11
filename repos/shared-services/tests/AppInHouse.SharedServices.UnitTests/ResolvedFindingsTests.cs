using System.Net;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure.Email;
using AppInHouse.SharedServices.UnitTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AppInHouse.SharedServices.UnitTests;

/// <summary>TASK-033 finding #2: the stored SMTP password goes only to the server and login it was saved for.</summary>
public class SmtpPasswordInheritanceTests
{
    private const string Settings = """{"host":"smtp.example.com","port":587,"username":"mailer","senderEmail":"noreply@example.com"}""";

    private static SaveChannelConfigurationCommand Smtp(string settings, string? secret = null, string name = "Mail") =>
        new("p", ChannelType.Email, ProviderType.Smtp, name, settings, secret);

    private static async Task<ManagerFixture> WithStoredPasswordAsync()
    {
        var f = new ManagerFixture();
        await f.Manager.SaveAsync(Smtp(Settings, "Stored-P@ssw0rd-1"));
        return f;
    }

    [Theory]
    [InlineData("""{"host":"smtp.evil.example","port":587,"username":"mailer","senderEmail":"noreply@example.com"}""")]   // another server
    [InlineData("""{"host":"smtp.example.com","port":587,"username":"admin","senderEmail":"noreply@example.com"}""")]      // another login
    [InlineData("""{"host":"smtp.example.com","port":587,"senderEmail":"noreply@example.com"}""")]                         // no login at all
    [InlineData("""{"host":"smtp.example.com","port":587,"username":"MAILER","senderEmail":"noreply@example.com"}""")]    // user names are case sensitive
    public async Task ChangingTheHostOrTheUsername_WithoutANewPassword_IsRefused_AndNothingIsSaved(string changed)
    {
        var f = await WithStoredPasswordAsync();

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.Manager.SaveAsync(Smtp(changed)));

        Assert.Contains("Secret", error.Errors.Keys);
        Assert.Contains("enter the password again", error.Errors["Secret"][0]);
        Assert.Single(f.Repository.Rows);   // no second version
    }

    [Theory]
    [InlineData("""{"host":"smtp.evil.example","port":587,"username":"mailer"}""")]
    [InlineData("""{"host":"smtp.example.com","username":"someone-else"}""")]
    public async Task WithANewPassword_TheChangeIsAccepted_AndTheNewPasswordIsTheOneStored(string changed)
    {
        var f = await WithStoredPasswordAsync();

        var saved = await f.Manager.SaveAsync(Smtp(changed, "Brand-new-P@ss"));

        Assert.Equal(2, saved.Version);
        Assert.Equal("Brand-new-P@ss", f.Protector.Unprotect(f.Repository.Rows.Single(r => r.Version == 2).EncryptedSecret!));
    }

    [Theory]
    [InlineData("""{"host":"smtp.example.com","port":2525,"username":"mailer","senderEmail":"other@example.com","senderName":"New"}""")]   // only other fields
    [InlineData("""{"host":"  SMTP.Example.COM ","port":587,"username":"mailer"}""")]                                                       // the same host, written differently
    public async Task ChangingOtherSettings_KeepsThePassword(string changed)
    {
        var f = await WithStoredPasswordAsync();

        var saved = await f.Manager.SaveAsync(Smtp(changed));

        Assert.True(saved.HasSecret);
        Assert.Equal("Stored-P@ssw0rd-1", f.Protector.Unprotect(f.Repository.Rows.Single(r => r.Version == 2).EncryptedSecret!));
    }

    [Fact]
    public async Task TheMaskTheFormEchoesBack_IsNotAPassword_SoAChangedHostStillNeedsOne()
    {
        var f = await WithStoredPasswordAsync();

        await Assert.ThrowsAsync<ValidationException>(() => f.Manager.SaveAsync(Smtp("""{"host":"smtp.evil.example","username":"mailer"}""", "••••••••")));
    }

    [Fact]
    public async Task WhenNoPasswordWasStored_ThereIsNothingToInherit_SoAChangedHostIsFine()
    {
        var f = new ManagerFixture();
        await f.Manager.SaveAsync(Smtp(Settings));   // a relay that needs no login

        var saved = await f.Manager.SaveAsync(Smtp("""{"host":"relay.other.example","port":25}"""));

        Assert.Equal(2, saved.Version);
        Assert.False(saved.HasSecret);
    }

    [Fact]
    public async Task TheRuleIsPerSeries_AnotherConfigurationStartsFresh()
    {
        var f = await WithStoredPasswordAsync();

        var other = await f.Manager.SaveAsync(Smtp("""{"host":"smtp.other.example","username":"x"}""", name: "Second mail"));

        Assert.Equal(1, other.Version);
    }

    [Fact]
    public async Task ADraftThatKeptThePassword_ThenActivated_StillHasItsOwnHostAndLogin()
    {
        var f = await WithStoredPasswordAsync();
        var draft = await f.Manager.SaveAsync(Smtp("""{"host":"smtp.example.com","port":465,"username":"mailer"}"""));

        var activated = await f.Manager.ActivateAsync(draft.Id);

        Assert.Equal(465, activated.Settings.GetProperty("port").GetInt32());
        Assert.True(activated.HasSecret);
    }
}

/// <summary>TASK-034 finding #2: a Discord or Slack webhook address pasted into an ordinary setting is caught.</summary>
public class WebhookInSettingsTests
{
    private static readonly string Discord = "https://discord.com/api/webhooks/" + "123456789012345678" + "/" + "fake-webhook-token-for-tests-0123456789";
    private static readonly string DiscordApp = "https://discordapp.com/api/webhooks/" + "123456789012345678" + "/" + "fake-webhook-token-for-tests-0123456789";
    private static readonly string Slack = "https://hooks.slack.com/services/" + "T0FAKE000" + "/" + "B0FAKE000" + "/" + "fakewebhooktokenfortests0123";

    private static IReadOnlyDictionary<string, string[]> Check(string settings, ChannelType channel = ChannelType.Chat, ProviderType provider = ProviderType.DiscordWebhook) =>
        ChannelConfigurationValidator.Validate(new SaveChannelConfigurationCommand("p", channel, provider, "Chat", settings, ChannelConfigurationValidatorTests.SecretFor(provider)));

    [Theory]
    [InlineData("avatarUrl")]
    [InlineData("defaultUsername")]
    [InlineData("note")]
    [InlineData("description")]
    public void ADiscordWebhook_InAnOrdinarySetting_IsFound(string property)
    {
        var errors = Check(JsonSerializer.Serialize(new Dictionary<string, string> { [property] = Discord }));

        Assert.Contains(errors["SettingsJson"], e => e.Contains("looks like a secret") && e.Contains(property));
        Assert.DoesNotContain(Discord, string.Join(' ', errors.Values.SelectMany(v => v)));   // the message never repeats it
    }

    [Fact]
    public void TheOlderDiscordAppDomain_ASlackWebhook_AndOneInsideLongerText_AreFoundToo()
    {
        Assert.Contains("SettingsJson", Check(JsonSerializer.Serialize(new { avatarUrl = DiscordApp })).Keys);
        Assert.Contains("SettingsJson", Check(JsonSerializer.Serialize(new { defaultChannel = Slack }), provider: ProviderType.SlackWebhook).Keys);
        Assert.Contains("SettingsJson", Check(JsonSerializer.Serialize(new { note = $"post to {Slack} when done" })).Keys);
    }

    [Fact]
    public void AWebhookNestedInAnObjectOrAnArray_IsFound()
    {
        Assert.Contains(Check(JsonSerializer.Serialize(new { options = new { links = new[] { "https://example.com/ok", Discord } } }))["SettingsJson"], e => e.Contains("options.links[1]"));
    }

    [Theory]
    [InlineData("https://cdn.discordapp.com/avatars/123/abc.png")]       // a Discord address that is not a webhook
    [InlineData("https://discord.com/channels/123/456")]
    [InlineData("https://hooks.slack.com/docs/help")]
    [InlineData("https://example.com/api/webhooks/123/abc")]            // a webhook-shaped path on some other host is not a platform webhook
    public void OrdinaryAddresses_ArePassed(string address)
    {
        var errors = Check(JsonSerializer.Serialize(new { avatarUrl = address }));

        Assert.DoesNotContain(errors.GetValueOrDefault("SettingsJson") ?? [], e => e.Contains("looks like a secret"));
    }

    [Fact]
    public void TheWebhookAsTheSecret_IsStillTheRightPlace_AndIsNotRefusedThere() =>
        Assert.Empty(Check("{}"));
}

/// <summary>TASK-035 minor #5: an absolute <c>rootPath</c> is refused on save unless the host allows absolute paths.</summary>
public class AbsoluteRootPathTests
{
    private static SaveChannelConfigurationCommand Disk(string rootPath) =>
        new("p", ChannelType.Storage, ProviderType.LocalDisk, "Storage", JsonSerializer.Serialize(new { rootPath }));

    [Theory]
    [InlineData("/var/lib/files")]
    [InlineData("\\\\server\\share\\files")]
    [InlineData("C:\\data\\files")]
    [InlineData("d:/data/files")]
    [InlineData("\\data")]
    public void AnAbsolutePath_IsRefusedByDefault_OnEveryPlatformSyntax(string rootPath)
    {
        var errors = ChannelConfigurationValidator.Validate(Disk(rootPath));

        Assert.Contains(errors["SettingsJson"], e => e.Contains("absolute paths are not allowed"));
    }

    [Theory]
    [InlineData("/var/lib/files")]
    [InlineData("C:\\data\\files")]
    public void WhenTheHostAllowsIt_AnAbsolutePathIsAccepted(string rootPath) =>
        Assert.Empty(ChannelConfigurationValidator.Validate(Disk(rootPath), new ConfigurationRules(AllowAbsoluteStoragePaths: true)));

    [Theory]
    [InlineData("uploads")]
    [InlineData("a/b/c")]
    [InlineData("files.v2")]
    [InlineData("c-data")]          // a name that only starts with a letter is not a drive
    public void ARelativeFolderName_IsAlwaysAccepted(string rootPath)
    {
        Assert.Empty(ChannelConfigurationValidator.Validate(Disk(rootPath)));
        Assert.Empty(ChannelConfigurationValidator.Validate(Disk(rootPath), new ConfigurationRules(true)));
    }

    [Fact]
    public void TheRuleIsAboutLocalDiskStorageOnly()
    {
        Assert.Empty(ChannelConfigurationValidator.Validate(new SaveChannelConfigurationCommand("p", ChannelType.Chat, ProviderType.Mock, "Chat", """{"defaultChatId":"-100"}""")));
        Assert.DoesNotContain(
            ChannelConfigurationValidator.Validate(new SaveChannelConfigurationCommand("p", ChannelType.Storage, ProviderType.S3, "S", """{"bucket":"my-bucket"}""", ChannelConfigurationValidatorTests.SecretFor(ProviderType.S3))).Keys,
            key => key == "SettingsJson");
    }

    [Fact]
    public async Task TheManager_UsesTheHostsRules_WhenSaving()
    {
        var f = new ManagerFixture();
        var strict = f.Manager;
        var lenient = new ChannelConfigManager(f.Repository, f.Protector, f.Audit, f.UnitOfWork, f.Clock, new ConfigurationRules(true));

        await Assert.ThrowsAsync<ValidationException>(() => strict.SaveAsync(Disk("/srv/files")));
        Assert.Empty(f.Repository.Rows);
        Assert.Equal(1, (await lenient.SaveAsync(Disk("/srv/files"))).Version);
    }

    [Fact]
    public void WithoutRules_TheStrictestAreUsed()
    {
        Assert.False(ConfigurationRules.Default.AllowAbsoluteStoragePaths);
        Assert.NotEmpty(ChannelConfigurationValidator.Validate(Disk("/etc"), rules: null));
    }
}

/// <summary>TASK-035 minor #3: storage has its own switch for private networks, independent of the email one.</summary>
public class OutboundHostPolicyTests
{
    private static OutboundHostPolicy Policy(bool allowPrivate, string host, string address) =>
        new(() => allowPrivate, new FakeHostResolver(new() { [host] = [address] }));

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.20")]
    [InlineData("172.16.0.9")]
    [InlineData("127.0.0.1")]
    public async Task PrivateAddresses_AreAllowedOnlyWhenThisPolicysSwitchIsOn(string address)
    {
        Assert.NotNull(await Policy(false, "minio.local", address).CheckAsync("minio.local", default));
        Assert.Null(await Policy(true, "minio.local", address).CheckAsync("minio.local", default));
    }

    [Theory]
    [InlineData("169.254.169.254")]   // the cloud metadata address
    [InlineData("224.0.0.1")]
    [InlineData("0.0.0.0")]
    public async Task LinkLocalMulticastAndUnspecified_AreNeverAllowed_WhateverTheSwitch(string address) =>
        Assert.NotNull(await Policy(true, "host.example", address).CheckAsync("host.example", default));

    [Fact]
    public async Task AnAddressOnThePublicInternet_IsAlwaysAllowed() =>
        Assert.Null(await Policy(false, "s3.example.test", "93.184.216.34").CheckAsync("s3.example.test", default));

    [Fact]
    public async Task TheSwitchIsReadEachTime_SoItCanChangeWithoutAnotherPolicy()
    {
        var allow = false;
        var policy = new OutboundHostPolicy(() => allow, new FakeHostResolver(new() { ["minio.local"] = ["10.0.0.5"] }));

        Assert.NotNull(await policy.CheckAsync("minio.local", default));
        allow = true;
        Assert.Null(await policy.CheckAsync("minio.local", default));
    }

    [Fact]
    public async Task TheEmailAndStoragePolicies_AreIndependent()
    {
        var resolver = new FakeHostResolver(new() { ["internal.local"] = ["10.0.0.5"] });
        var email = new OutboundHostPolicy(Options.Create(new EmailOptions { AllowPrivateNetworkHosts = true }), resolver);
        var storage = new OutboundHostPolicy(() => false, resolver);

        Assert.Null(await email.CheckAsync("internal.local", default));        // mail may reach the internal network
        Assert.NotNull(await storage.CheckAsync("internal.local", default));   // storage may not, and the other way round
        var storageOn = new OutboundHostPolicy(() => true, resolver);
        var emailOff = new OutboundHostPolicy(Options.Create(new EmailOptions { AllowPrivateNetworkHosts = false }), resolver);
        Assert.Null(await storageOn.CheckAsync("internal.local", default));
        Assert.NotNull(await emailOff.CheckAsync("internal.local", default));
    }

    [Fact]
    public void StorageOptions_HaveTheirOwnSwitch_OffByDefault()
    {
        Assert.False(new AppInHouse.SharedServices.Infrastructure.Storage.StorageOptions().AllowPrivateNetworkHosts);
        Assert.False(new EmailOptions().AllowPrivateNetworkHosts);
    }
}

/// <summary>The client SDK is complete, and registers itself the way the task shows.</summary>
public class ClientSdkCompletenessTests
{
    [Theory]
    [InlineData("ListConfigurationsAsync")]
    [InlineData("GetConfigurationAsync")]
    [InlineData("SaveConfigurationAsync")]
    [InlineData("ActivateConfigurationAsync")]
    [InlineData("RollbackConfigurationAsync")]
    [InlineData("SendEmailAsync")]
    [InlineData("SendTestEmailAsync")]
    [InlineData("GetEmailDeliveryLogsAsync")]
    [InlineData("SendChatMessageAsync")]
    [InlineData("SendTestChatMessageAsync")]
    [InlineData("GetChatDeliveryLogsAsync")]
    [InlineData("UploadFileAsync")]
    [InlineData("DownloadFileAsync")]
    [InlineData("GetThumbnailAsync")]
    [InlineData("GetFileMetadataAsync")]
    [InlineData("ListFilesAsync")]
    [InlineData("DeleteFileAsync")]
    [InlineData("TestStorageAsync")]
    [InlineData("GetMediaLogsAsync")]
    [InlineData("CreateApiKeyAsync")]
    [InlineData("ListApiKeysAsync")]
    [InlineData("RevokeApiKeyAsync")]
    public void TheClientHasEveryMethodOfTheTask(string method) =>
        Assert.NotNull(typeof(ISharedServicesClient).GetMethod(method));

    private sealed class Handler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task AddSharedServicesClient_GivesTheClientItsAddressAndKey_AsTheTaskShows()
    {
        var handler = new Handler();
        var services = new ServiceCollection();
        services.AddSharedServicesClient(options =>
        {
            options.BaseAddress = new Uri("http://localhost:5030");
            options.ApiKey = "aih_live_for_a_test_0123456789abcdefghijkl";
        });
        services.AddHttpClient<ISharedServicesClient, SharedServicesClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<ISharedServicesClient>();
        await client.ListApiKeysAsync("identity");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:5030/api/v1/api-keys?projectCode=identity", request.RequestUri!.ToString());
        Assert.Equal("aih_live_for_a_test_0123456789abcdefghijkl", request.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public void TheAddressCanBeGivenAsAStringOrAsAUri()
    {
        var options = new SharedServicesClientOptions { BaseUrl = "https://shared.example.com:8443" };
        Assert.Equal(new Uri("https://shared.example.com:8443"), options.BaseAddress);

        options.BaseAddress = new Uri("http://other:5030");
        Assert.Equal("http://other:5030/", options.BaseUrl);
        Assert.Throws<ArgumentNullException>(() => options.BaseAddress = null!);
    }

    [Fact]
    public async Task TheNewApiKeyAndAuditCalls_UseTheirEndpoints_AndTheirQueries()
    {
        var handler = new Handler();
        var client = new SharedServicesClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") });
        var id = Guid.Parse("7f2d6a1e-0000-0000-0000-000000000009");
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        await client.ListApiKeysAsync();
        await client.ListAuditLogsAsync("apikey:aih", "CreateApiKey", from, from.AddDays(1), 25);
        await client.GetAuditActionsAsync();
        handler.Requests.Clear();   // the calls below answer with objects, which the stub does not make: only the requests matter

        foreach (var call in new Func<Task>[]
        {
            () => client.RevokeApiKeyAsync(id), () => client.RotateApiKeyAsync(id, 3),
            () => client.CreateApiKeyAsync(new CreateApiKeyRequest("identity", "Identity", ["email:send"], 90)),
        })
        {
            try { await call(); }
            catch (Exception ex) when (ex is JsonException or SharedServicesApiException) { }
        }

        Assert.Equal(
            [($"/api/v1/api-keys/{id}/revoke", "POST"), ($"/api/v1/api-keys/{id}/rotate?graceDays=3", "POST"), ("/api/v1/api-keys", "POST")],
            handler.Requests.Select(r => (r.RequestUri!.PathAndQuery, r.Method.Method)));
    }

    [Fact]
    public async Task TheAuditQuery_CarriesEveryFilter()
    {
        var handler = new Handler();
        var client = new SharedServicesClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") });
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        await client.ListAuditLogsAsync("apikey:aih", "CreateApiKey", from, from.AddDays(1), 25);

        var query = Uri.UnescapeDataString(handler.Requests.Single().RequestUri!.Query);
        Assert.Contains("limit=25", query);
        Assert.Contains("actor=apikey:aih", query);
        Assert.Contains("action=CreateApiKey", query);
        Assert.Contains("from=2026-11-01T00:00:00.0000000+00:00", query);
        Assert.Contains("to=2026-11-02T00:00:00.0000000+00:00", query);
    }

    [Fact]
    public void TheCreatedKeyResponse_DoesNotPrintTheKey()
    {
        var key = new ApiKeyDto(Guid.NewGuid(), "p", "n", "aih_live_abc123", ["email:send"], "Active", DateTimeOffset.UtcNow, null, null);

        var text = new CreateApiKeyResponse(key, "aih_live_abc123SECRETPARTSECRETPARTSECRETPART").ToString();

        Assert.DoesNotContain("SECRETPART", text);
        Assert.DoesNotContain("SECRETPART", new RotateApiKeyResponse(key, "aih_live_abc123SECRETPART", key).ToString());
    }
}
