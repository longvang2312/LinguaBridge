using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using static AppInHouse.SharedServices.IntegrationTests.Support.PlatformApi;

namespace AppInHouse.SharedServices.IntegrationTests;

public class ProjectApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Projects = "/api/v1/projects";

    private static async Task<JsonElement> CreateAsync(HttpClient admin, string code, object? extra = null)
    {
        var response = await admin.PostAsJsonAsync(Projects, new { projectCode = code, name = "Project " + code, description = "d" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient admin, string code, object body) => admin.PutAsJsonAsync($"{Projects}/{code}", body);

    // ------------------------------------------------------------------------------------------------ the projects

    [Fact]
    public async Task TheCoreProjects_AreThere_DefaultFirst_WithTheirEnvironmentAndQuotas()
    {
        var admin = await AdminAsync(factory);

        var response = await admin.GetAsync(Projects);
        var projects = (await ReadAsync(response)).EnumerateArray().ToDictionary(p => p.GetProperty("projectCode").GetString()!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("default", (await ReadAsync(await admin.GetAsync(Projects))).EnumerateArray().First().GetProperty("projectCode").GetString());
        Assert.True(new[] { "default", "identity", "linguabridge", "crm" }.All(projects.ContainsKey));
        Assert.Equal(("Production", "Staging"), (projects["linguabridge"].GetProperty("environment").GetString(), projects["crm"].GetProperty("environment").GetString()));
        Assert.Equal(JsonValueKind.Null, projects["default"].GetProperty("dailyEmailQuota").ValueKind);   // the platform is not limited
        Assert.Equal(10_000, projects["linguabridge"].GetProperty("dailyEmailQuota").GetInt32());
        Assert.Equal(50L * 1024 * 1024 * 1024, projects["linguabridge"].GetProperty("storageQuotaBytes").GetInt64());
        Assert.All(new[] { "default", "identity", "linguabridge", "crm" }, code => Assert.True(projects[code].GetProperty("isActive").GetBoolean()));
    }

    [Fact]
    public async Task ANewProject_IsStored_WithItsCodeInLowerCase_AndAuditedAndListed()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();

        var response = await admin.PostAsJsonAsync(Projects, new { projectCode = code.ToUpperInvariant(), name = "Billing", description = "Invoices", environment = "Production", dailyEmailQuota = 100, dailyChatQuota = 20, storageQuotaBytes = 1_048_576 });
        var project = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/v1/projects/{code}/usage", response.Headers.Location!.ToString());
        Assert.Equal((code, "Billing", "Production", 100, 20, 1_048_576L, true), (project.GetProperty("projectCode").GetString(), project.GetProperty("name").GetString(), project.GetProperty("environment").GetString(), project.GetProperty("dailyEmailQuota").GetInt32(), project.GetProperty("dailyChatQuota").GetInt32(), project.GetProperty("storageQuotaBytes").GetInt64(), project.GetProperty("isActive").GetBoolean()));
        Assert.Contains((await ReadAsync(await admin.GetAsync(Projects))).EnumerateArray(), p => p.GetProperty("projectCode").GetString() == code);
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.Action == "CreateProject" && a.ResourceId == code)));
    }

    [Fact]
    public async Task AQuotaLeftOut_IsNoLimit_AndTheEnvironmentDefaultsToStaging()
    {
        var created = await CreateAsync(await AdminAsync(factory), NewProject());

        Assert.Equal(JsonValueKind.Null, created.GetProperty("dailyEmailQuota").ValueKind);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("storageQuotaBytes").ValueKind);
        Assert.Equal("Staging", created.GetProperty("environment").GetString());
    }

    [Theory]
    [InlineData("", "Name", 0)]
    [InlineData("Has Space", "Name", 0)]
    [InlineData("-leading", "Name", 0)]
    [InlineData("good-code", "", 0)]
    [InlineData("good-code", "Name", -1)]
    public async Task ABadProject_Is400_AndNothingIsStored(string code, string name, int emailQuota)
    {
        var admin = await AdminAsync(factory);
        var before = await factory.WithDbAsync(db => db.ProjectTenants.CountAsync());

        var response = await admin.PostAsJsonAsync(Projects, new { projectCode = code, name, dailyEmailQuota = emailQuota });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await factory.WithDbAsync(db => db.ProjectTenants.CountAsync()));
    }

    [Fact]
    public async Task ACodeThatExists_Is409_EvenInAnotherCase()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();
        await CreateAsync(admin, code);

        var again = await admin.PostAsJsonAsync(Projects, new { projectCode = code.ToUpperInvariant(), name = "Again" });
        var seeded = await admin.PostAsJsonAsync(Projects, new { projectCode = "identity", name = "Again" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, seeded.StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ changing one

    [Fact]
    public async Task AnUpdate_ReplacesTheEditableFields_AndIsAudited()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();
        await CreateAsync(admin, code);

        var response = await PutAsync(admin, code, new { name = "Renamed", description = "new", environment = "Production", dailyEmailQuota = 5, dailyChatQuota = (int?)null, storageQuotaBytes = 4096L, isActive = false });
        var project = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("Renamed", "Production", 5, false), (project.GetProperty("name").GetString(), project.GetProperty("environment").GetString(), project.GetProperty("dailyEmailQuota").GetInt32(), project.GetProperty("isActive").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, project.GetProperty("dailyChatQuota").ValueKind);
        Assert.Equal(code, project.GetProperty("projectCode").GetString());   // the code never changes
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.Action == "UpdateProject" && a.ResourceId == code)));
    }

    [Fact]
    public async Task UpdatingAnUnknownProject_Is404_ThePlatformCannotBeSwitchedOff_AndANegativeQuotaIs400()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();
        await CreateAsync(admin, code);

        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(admin, "nope", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, "default", new { name = "Platform", isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, code, new { name = "x", dailyChatQuota = -5 })).StatusCode);
        Assert.True(await factory.WithDbAsync(db => db.ProjectTenants.AnyAsync(p => p.ProjectCode == "default" && p.IsActive)));
    }

    // ------------------------------------------------------------------------------------------------ who may

    [Fact]
    public async Task OnlyThePlatform_CreatesAndChangesProjects()
    {
        var code = NewProject();
        var project = await ProjectClientAsync(factory, code, ApiScopes.ConfigRead, ApiScopes.ConfigWrite);
        var reader = factory.ClientWithKey((await factory.CreateKeyAsync(ApiScopes.ConfigRead)).PlainKey);

        Assert.Equal(HttpStatusCode.Forbidden, (await project.PostAsJsonAsync(Projects, new { projectCode = NewProject(), name = "Sneaky" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await project.PutAsJsonAsync($"{Projects}/{code}", new { name = "Self service", dailyEmailQuota = 999999 })).StatusCode);   // a project cannot raise its own quota
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Projects, new { projectCode = NewProject(), name = "Reader" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).PostAsJsonAsync(Projects, new { projectCode = NewProject(), name = "Anonymous" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).GetAsync(Projects)).StatusCode);
    }

    [Fact]
    public async Task AKeyOfOneProject_SeesOnlyItsOwnProjectAndItsOwnUsage()
    {
        var admin = await AdminAsync(factory);
        var mine = NewProject();
        var other = NewProject();
        await CreateAsync(admin, mine);
        await CreateAsync(admin, other);
        var client = await ProjectClientAsync(factory, mine, ApiScopes.ConfigRead);

        var listed = (await ReadAsync(await client.GetAsync(Projects))).EnumerateArray().ToList();
        var ownUsage = await client.GetAsync($"{Projects}/{mine}/usage");
        var foreignUsage = await client.GetAsync($"{Projects}/{other}/usage");

        Assert.Equal([mine], listed.Select(p => p.GetProperty("projectCode").GetString()));
        Assert.Equal(HttpStatusCode.OK, ownUsage.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignUsage.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Projects}/{other}/usage")).StatusCode);
    }

    [Fact]
    public async Task TheUsageOfAnUnknownProject_Is404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await (await AdminAsync(factory)).GetAsync($"{Projects}/nope/usage")).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ usage

    [Fact]
    public async Task TheUsage_CountsTodaysSuccessfulSends_TheFilesAndTheActiveKeys_NextToTheQuotas()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();
        await admin.PostAsJsonAsync(Projects, new { projectCode = code, name = "Usage", dailyEmailQuota = 10, dailyChatQuota = 5, storageQuotaBytes = 10_000_000 });
        await ActivateMockEmailAsync(admin, code);
        await ActivateMockChatAsync(admin, code);
        await ActivateAsync(admin, code, "Storage", "LocalDisk", new { rootPath = "uploads" });
        var client = await ProjectClientAsync(factory, code, ApiScopes.EmailSend, ApiScopes.ChatSend, ApiScopes.StorageUpload, ApiScopes.ConfigRead);
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/emails/send", new { to = NewRecipient(), subject = "s", textBody = "t" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/chat/send", new { text = "hello" })).StatusCode);
        var png = TestFiles.Png();
        using var form = new MultipartFormDataContent { { new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } }, "file", "a.png" } };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/v1/storage/upload", form)).StatusCode);

        var usage = await ReadAsync(await client.GetAsync($"{Projects}/{code}/usage"));

        Assert.Equal(code, usage.GetProperty("projectCode").GetString());
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), usage.GetProperty("date").GetString());
        Assert.Equal((3, 10, 1, 5), (usage.GetProperty("emailsSentToday").GetInt32(), usage.GetProperty("dailyEmailQuota").GetInt32(), usage.GetProperty("chatSentToday").GetInt32(), usage.GetProperty("dailyChatQuota").GetInt32()));
        Assert.Equal((png.LongLength, 10_000_000L, 1), (usage.GetProperty("storageBytes").GetInt64(), usage.GetProperty("storageQuotaBytes").GetInt64(), usage.GetProperty("fileCount").GetInt32()));
        Assert.Equal(1, usage.GetProperty("activeApiKeys").GetInt32());
    }

    // ------------------------------------------------------------------------------------------------ quotas

    [Fact]
    public async Task TheEmailQuota_StopsTheNextSend_With429_AndOnlyForThatProject()
    {
        var admin = await AdminAsync(factory);
        var (limited, free) = (NewProject(), NewProject());
        await admin.PostAsJsonAsync(Projects, new { projectCode = limited, name = "Limited", dailyEmailQuota = 2 });
        await admin.PostAsJsonAsync(Projects, new { projectCode = free, name = "Free", dailyEmailQuota = 1000 });
        await ActivateMockEmailAsync(admin, limited);
        await ActivateMockEmailAsync(admin, free);
        var client = await ProjectClientAsync(factory, limited, ApiScopes.EmailSend);
        var other = await ProjectClientAsync(factory, free, ApiScopes.EmailSend);
        object Mail() => new { to = NewRecipient(), subject = "s", textBody = "t" };

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);
        var third = await client.PostAsJsonAsync("/api/v1/emails/send", Mail());
        var problem = await ReadAsync(third);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(("Daily quota exceeded", 2, 2), (problem.GetProperty("title").GetString(), problem.GetProperty("quota").GetInt32(), problem.GetProperty("used").GetInt32()));
        Assert.Contains("00:00 UTC", problem.GetProperty("detail").GetString());
        Assert.True(third.Headers.TryGetValues("Retry-After", out var retry) && int.Parse(retry.Single()) is > 0 and <= 86400);
        Assert.Equal("application/problem+json", third.Content.Headers.ContentType!.MediaType);   // RFC 7807
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);   // another project is not affected
        Assert.Equal(2, await factory.WithDbAsync(db => db.DeliveryLogs.CountAsync(l => l.ProjectCode == limited && l.Status == DeliveryStatus.Sent)));
    }

    [Fact]
    public async Task TheChatQuota_IsSeparateFromTheEmailQuota()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();
        await admin.PostAsJsonAsync(Projects, new { projectCode = code, name = "Chatty", dailyEmailQuota = 5, dailyChatQuota = 1 });
        await ActivateMockEmailAsync(admin, code);
        await ActivateMockChatAsync(admin, code);
        var client = await ProjectClientAsync(factory, code, ApiScopes.EmailSend, ApiScopes.ChatSend);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/chat/send", new { text = "one" })).StatusCode);
        var second = await client.PostAsJsonAsync("/api/v1/chat/send", new { text = "two" });

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/emails/send", new { to = NewRecipient(), subject = "s", textBody = "t" })).StatusCode);
        Assert.Equal(1, MockChat(factory).SentMessages.Count(m => m.ProjectCode == code));
    }

    [Fact]
    public async Task OnlyDeliveredMessagesUseUpTheQuota_AProviderThatFailsCostsNothing()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();
        await admin.PostAsJsonAsync(Projects, new { projectCode = code, name = "Flaky", dailyEmailQuota = 2 });
        await ActivateSendGridAsync(admin, code);
        var client = await ProjectClientAsync(factory, code, ApiScopes.EmailSend);
        object Mail() => new { to = NewRecipient(), subject = "s", textBody = "t" };
        factory.SendGrid.Respond = _ => ServerError();
        try
        {
            for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);
        }
        finally
        {
            factory.SendGrid.Respond = _ => Accepted();
        }

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);
    }

    [Fact]
    public async Task ARaisedQuota_LetsTheProjectSendAgain_AQuotaOfZeroStopsItAtOnce()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();
        await admin.PostAsJsonAsync(Projects, new { projectCode = code, name = "Raised", dailyEmailQuota = 0 });
        await ActivateMockEmailAsync(admin, code);
        var client = await ProjectClientAsync(factory, code, ApiScopes.EmailSend);
        object Mail() => new { to = NewRecipient(), subject = "s", textBody = "t" };

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);
        await PutAsync(admin, code, new { name = "Raised", dailyEmailQuota = 1 });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);
        await PutAsync(admin, code, new { name = "Raised" });   // no quota any more
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);
    }

    [Fact]
    public async Task YesterdaysDeliveries_DoNotCountToday()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();
        await admin.PostAsJsonAsync(Projects, new { projectCode = code, name = "Daily", dailyEmailQuota = 1 });
        await ActivateMockEmailAsync(admin, code);
        var yesterday = DateTimeOffset.UtcNow.Date.AddMinutes(-5);   // 23:55 yesterday, UTC
        await factory.WithDbAsync(async db =>
        {
            db.DeliveryLogs.Add(DeliveryLog.Create(code, ChannelType.Email, "old@example.com", DeliveryStatus.Sent, null, 5, yesterday));
            return await db.SaveChangesAsync();
        });
        var client = await ProjectClientAsync(factory, code, ApiScopes.EmailSend);

        var response = await client.PostAsJsonAsync("/api/v1/emails/send", new { to = NewRecipient(), subject = "s", textBody = "t" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheStorageQuota_RefusesTheUploadThatDoesNotFit_With429AndNoRetryAfter_AndDeletingFreesRoom()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();
        var png = TestFiles.Png();
        var quota = png.LongLength * 2 + 10;
        await admin.PostAsJsonAsync(Projects, new { projectCode = code, name = "Storage", storageQuotaBytes = quota });
        await ActivateAsync(admin, code, "Storage", "LocalDisk", new { rootPath = "uploads" });
        var client = await ProjectClientAsync(factory, code, ApiScopes.StorageUpload, ApiScopes.StorageDelete, ApiScopes.StorageRead);
        MultipartFormDataContent Form() => new() { { new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } }, "file", "a.png" } };

        var first = await client.PostAsync("/api/v1/storage/upload", Form());
        var second = await client.PostAsync("/api/v1/storage/upload", Form());
        var third = await client.PostAsync("/api/v1/storage/upload", Form());
        var problem = await ReadAsync(third);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests), (first.StatusCode, second.StatusCode, third.StatusCode));
        Assert.Equal("Storage quota exceeded", problem.GetProperty("title").GetString());
        Assert.Equal((quota, png.LongLength * 2), (problem.GetProperty("quota").GetInt64(), problem.GetProperty("used").GetInt64()));
        Assert.False(third.Headers.Contains("Retry-After"));   // waiting does not help; deleting does
        Assert.Equal(2, await factory.WithDbAsync(db => db.StoredFiles.CountAsync(f => f.ProjectCode == code)));

        var firstId = (await ReadAsync(first)).GetProperty("file").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/storage/files/{firstId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/v1/storage/upload", Form())).StatusCode);
    }

    [Fact]
    public async Task AProjectThatIsFull_IsRefusedBeforeItsFileIsEvenRead()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();
        await admin.PostAsJsonAsync(Projects, new { projectCode = code, name = "Full", storageQuotaBytes = 0 });
        await ActivateAsync(admin, code, "Storage", "LocalDisk", new { rootPath = "uploads" });
        var client = await ProjectClientAsync(factory, code, ApiScopes.StorageUpload);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()) { Headers = { ContentType = new("image/png") } }, "file", "a.png" } };

        var response = await client.PostAsync("/api/v1/storage/upload", form);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.StoredFiles.CountAsync(f => f.ProjectCode == code)));
        Assert.False(await factory.WithDbAsync(db => db.MediaLogs.AnyAsync(l => l.ProjectCode == code)));   // refused at the door: nothing was received to log
    }

    [Fact]
    public async Task ADeactivatedProject_CanNeitherSendNorUpload_UntilItIsActivatedAgain()
    {
        var admin = await AdminAsync(factory);
        var code = NewProject();
        await admin.PostAsJsonAsync(Projects, new { projectCode = code, name = "Paused" });
        await ActivateMockEmailAsync(admin, code);
        await ActivateMockChatAsync(admin, code);
        await ActivateAsync(admin, code, "Storage", "LocalDisk", new { rootPath = "uploads" });
        var client = await ProjectClientAsync(factory, code, ApiScopes.EmailSend, ApiScopes.ChatSend, ApiScopes.StorageUpload);
        Task<HttpResponseMessage> Email() => client.PostAsJsonAsync("/api/v1/emails/send", new { to = NewRecipient(), subject = "s", textBody = "t" });
        Task<HttpResponseMessage> Upload() => client.PostAsync("/api/v1/storage/upload", new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()) { Headers = { ContentType = new("image/png") } }, "file", "a.png" } });

        await PutAsync(admin, code, new { name = "Paused", isActive = false });
        var email = await Email();
        var chat = await client.PostAsJsonAsync("/api/v1/chat/send", new { text = "hi" });
        var upload = await Upload();
        var notification = await client.PostAsJsonAsync("/api/v1/notifications/dispatch", new { templateCode = "PasswordReset", recipientEmail = NewRecipient(), data = Data(new { UserName = "a", AppName = "b", ActionUrl = "c", ExpiryMinutes = 1 }) });

        Assert.Equal(HttpStatusCode.Forbidden, email.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, chat.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, notification.StatusCode);
        Assert.Contains("deactivated", (await ReadAsync(email)).GetProperty("detail").GetString());

        await PutAsync(admin, code, new { name = "Paused", isActive = true });
        Assert.Equal(HttpStatusCode.OK, (await Email()).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Upload()).StatusCode);
    }

    [Fact]
    public async Task TheDefaultProject_IsNeverLimited()
    {
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, "default");
        try
        {
            for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/emails/send", new { to = NewRecipient(), subject = "s", textBody = "t" })).StatusCode);
        }
        finally
        {
            await factory.WithDbAsync(async db =>
            {
                await db.ChannelConfigurations.Where(c => c.ProjectCode == "default" && c.ChannelType == ChannelType.Email).ExecuteDeleteAsync();
                return 0;
            });
        }
    }
}
