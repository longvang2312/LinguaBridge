using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>
/// Project isolation across every endpoint: a key of one project (the attacker, "linguabridge" in the task's words) gets 403 on every
/// resource of another project (the victim, "identity"), changes nothing there, and still works on its own; the platform's "default"
/// key works on all of them. The projects get random names here only so that tests sharing a host do not see each other.
/// </summary>
public class ProjectIsolationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Configs = "/api/v1/channel-configurations";

    private static string NewProject(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    private sealed record World(
        string Victim, string Attacker, HttpClient VictimClient, HttpClient AttackerClient, HttpClient Master,
        Guid EmailConfig, Guid EmailDraft, Guid ChatConfig, Guid StorageConfig, Guid FileId, string VictimRoot);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task<Guid> SaveAsync(HttpClient client, string project, string channel, string provider, object settings, string name, bool activate)
    {
        var response = await client.PostAsJsonAsync(Configs, new { projectCode = project, channelType = channel, providerType = provider, name, settings });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ReadAsync(response)).GetProperty("id").GetGuid();
        if (activate) Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Configs}/{id}/activate", null)).StatusCode);
        return id;
    }

    private async Task<HttpClient> ProjectClientAsync(string project) =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync(project, null, [.. ApiScopes.Assignable])).PlainKey);

    private async Task<World> BuildAsync()
    {
        var victim = NewProject("identity");
        var attacker = NewProject("linguabridge");
        var master = factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, [.. ApiScopes.Assignable])).PlainKey);
        var victimClient = await ProjectClientAsync(victim);
        var attackerClient = await ProjectClientAsync(attacker);

        var root = "iso-" + victim;
        var email = await SaveAsync(master, victim, "Email", "Mock", new { senderEmail = "noreply@example.com" }, "Mail", activate: true);
        var draft = await SaveAsync(master, victim, "Email", "Mock", new { senderEmail = "next@example.com" }, "Mail", activate: false);
        var chat = await SaveAsync(master, victim, "Chat", "Mock", new { }, "Chat", activate: true);
        var storage = await SaveAsync(master, victim, "Storage", "LocalDisk", new { rootPath = root }, "Storage", activate: true);

        await victimClient.PostAsJsonAsync("/api/v1/emails/send", new { to = "someone@example.com", subject = "Hello", htmlBody = "<p>Hi</p>" });
        await victimClient.PostAsJsonAsync("/api/v1/notifications/channels/send", new { text = "hello" });
        var upload = await victimClient.PostAsync("/api/v1/storage/upload", new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()), "file", "secret-plans.png" } });
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var fileId = (await ReadAsync(upload)).GetProperty("file").GetProperty("id").GetGuid();

        // The attacker has a configuration of its own too, so "list without naming a project" has something to show.
        await SaveAsync(master, attacker, "Email", "Mock", new { senderEmail = "own@example.com" }, "Mail", activate: true);
        await SaveAsync(master, attacker, "Chat", "Mock", new { }, "Chat", activate: true);
        await SaveAsync(master, attacker, "Storage", "LocalDisk", new { rootPath = "iso-" + attacker }, "Storage", activate: true);
        return new World(victim, attacker, victimClient, attackerClient, master, email, draft, chat, storage, fileId, root);
    }

    private async Task<string> FingerprintAsync(string project) =>
        await factory.WithDbAsync(async db =>
        {
            var configs = (await db.ChannelConfigurations.AsNoTracking().Where(c => c.ProjectCode == project).OrderBy(c => c.Version).ThenBy(c => c.Name).ToListAsync())
                .Select(c => $"{c.Name}:{c.Version}:{c.Status}:{c.SettingsJson}:{c.LastTestStatus}:{c.LastTestedAt}");
            var files = (await db.StoredFiles.AsNoTracking().Where(f => f.ProjectCode == project).ToListAsync()).Select(f => f.Id.ToString());
            var mediaLogs = await db.MediaLogs.CountAsync(l => l.ProjectCode == project);
            var deliveries = await db.DeliveryLogs.CountAsync(l => l.ProjectCode == project);
            return string.Join('|', configs) + "#" + string.Join(',', files) + $"#{mediaLogs}#{deliveries}";
        });

    /// <summary>Every call of the platform that touches a project's resources. Each is made as the attacker, aimed at the victim.</summary>
    private static List<(string Name, Func<HttpClient, Task<HttpResponseMessage>> Call)> CallsAtTheVictim(World w) =>
    [
        ("list configurations of the victim", c => c.GetAsync($"{Configs}?projectCode={w.Victim}")),
        ("read a configuration by id", c => c.GetAsync($"{Configs}/{w.EmailConfig}")),
        ("save a configuration for the victim", c => c.PostAsJsonAsync(Configs, new { projectCode = w.Victim, channelType = "Email", providerType = "Mock", name = "Mail", settings = new { senderEmail = "evil@example.com" } })),
        ("activate a configuration of the victim", c => c.PostAsync($"{Configs}/{w.EmailDraft}/activate", null)),
        ("roll back a configuration of the victim", c => c.PostAsJsonAsync($"{Configs}/rollback", new { projectCode = w.Victim, channelType = "Email", name = "Mail" })),
        ("send an email for the victim", c => c.PostAsJsonAsync("/api/v1/emails/send", new { projectCode = w.Victim, to = "x@example.com", subject = "s", htmlBody = "b" })),
        ("send a chat message for the victim", c => c.PostAsJsonAsync("/api/v1/notifications/channels/send", new { projectCode = w.Victim, text = "hi" })),
        ("upload a file to the victim", c => c.PostAsync("/api/v1/storage/upload", new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()), "file", "a.png" }, { new StringContent(w.Victim), "projectCode" } })),
        ("test the victim's saved email configuration", c => c.PostAsJsonAsync("/api/v1/emails/test", new { testRecipient = "x@example.com", configurationId = w.EmailConfig })),
        ("test the victim's saved chat configuration", c => c.PostAsJsonAsync("/api/v1/notifications/channels/test", new { configurationId = w.ChatConfig })),
        ("test the victim's saved storage configuration", c => c.PostAsJsonAsync("/api/v1/storage/test", new { configurationId = w.StorageConfig })),
        ("read the victim's email delivery logs", c => c.GetAsync($"/api/v1/emails/delivery-logs?projectCode={w.Victim}")),
        ("read the victim's chat delivery logs", c => c.GetAsync($"/api/v1/notifications/channels/delivery-logs?projectCode={w.Victim}")),
        ("list the victim's files", c => c.GetAsync($"/api/v1/storage/files?projectCode={w.Victim}")),
        ("download the victim's file", c => c.GetAsync($"/api/v1/storage/files/{w.FileId}")),
        ("see the victim's thumbnail", c => c.GetAsync($"/api/v1/storage/files/{w.FileId}/thumbnail")),
        ("read the victim's file metadata", c => c.GetAsync($"/api/v1/storage/files/{w.FileId}/metadata")),
        ("delete the victim's file", c => c.DeleteAsync($"/api/v1/storage/files/{w.FileId}")),
        ("read the victim's media logs", c => c.GetAsync($"/api/v1/storage/media-logs?projectCode={w.Victim}")),
        ("list the victim's API keys", c => c.GetAsync($"/api/v1/api-keys?projectCode={w.Victim}")),
        ("read the audit log", c => c.GetAsync("/api/v1/audit-logs")),
    ];

    [Fact]
    public async Task AKeyOfOneProject_IsRefusedOnEveryResourceOfAnother_WithA403Problem_AndChangesNothing()
    {
        var w = await BuildAsync();
        var before = await FingerprintAsync(w.Victim);
        var wrong = new List<string>();

        foreach (var (name, call) in CallsAtTheVictim(w))
        {
            var response = await call(w.AttackerClient);
            if (response.StatusCode != HttpStatusCode.Forbidden) wrong.Add($"{name}: {(int)response.StatusCode}");
            else
            {
                var problem = await ReadAsync(response);   // RFC 7807
                Assert.Equal(403, problem.GetProperty("status").GetInt32());
                Assert.True(problem.TryGetProperty("title", out _), name);
            }
        }

        Assert.Empty(wrong);
        Assert.Equal(before, await FingerprintAsync(w.Victim));   // nothing of the victim's changed: not a version, a status, a test result, a file, a log
    }

    [Fact]
    public async Task ThePlatformKey_ReachesEveryResourceOfEveryProject()
    {
        var w = await BuildAsync();
        var refused = new List<string>();

        // The same calls, made by the platform's "default" key, in an order that lets each succeed (the delete goes last).
        foreach (var (name, call) in CallsAtTheVictim(w).OrderBy(c => c.Name.StartsWith("delete", StringComparison.Ordinal) ? 1 : 0))
        {
            var response = await call(w.Master);
            if ((int)response.StatusCode is >= 400 and not 409) refused.Add($"{name}: {(int)response.StatusCode}");   // 409 is a rollback with nothing earlier, not a refusal of access
        }

        Assert.Empty(refused);
    }

    [Fact]
    public async Task AKey_StillWorksOnItsOwnProject_AndSeesOnlyItsOwnWhenItNamesNone()
    {
        var w = await BuildAsync();
        var own = await ReadAsync(await w.AttackerClient.GetAsync(Configs));
        var ownConfig = own.EnumerateArray().First(c => c.GetProperty("channelType").GetString() == "Chat").GetProperty("id").GetGuid();
        var ownStorage = own.EnumerateArray().First(c => c.GetProperty("channelType").GetString() == "Storage").GetProperty("id").GetGuid();

        Assert.NotEmpty(own.EnumerateArray());
        Assert.All(own.EnumerateArray(), c => Assert.Equal(w.Attacker, c.GetProperty("projectCode").GetString()));   // no project named: its own, never the victim's
        Assert.Equal(HttpStatusCode.OK, (await w.AttackerClient.GetAsync($"{Configs}?projectCode={w.Attacker}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.AttackerClient.GetAsync($"{Configs}/{ownConfig}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await w.AttackerClient.PostAsJsonAsync(Configs, new { projectCode = w.Attacker, channelType = "Chat", providerType = "Mock", name = "Chat", settings = new { } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.AttackerClient.PostAsJsonAsync("/api/v1/notifications/channels/test", new { configurationId = ownConfig })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.AttackerClient.PostAsJsonAsync("/api/v1/storage/test", new { configurationId = ownStorage })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.AttackerClient.PostAsJsonAsync("/api/v1/notifications/channels/send", new { text = "mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.AttackerClient.GetAsync("/api/v1/notifications/channels/delivery-logs")).StatusCode);
    }

    [Fact]
    public async Task ATestFromMemory_NamesNoProject_SoAnyConfigWriteKeyMayRunIt_AndItTouchesNothingSaved()
    {
        var w = await BuildAsync();
        var before = await FingerprintAsync(w.Victim);

        var response = await w.AttackerClient.PostAsJsonAsync("/api/v1/notifications/channels/test", new { providerType = "Mock" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await FingerprintAsync(w.Victim));
    }

    [Fact]
    public async Task AKeyThatIsNotForThePlatform_CannotReadTheVictimsFileEvenWithEveryScope_ButAPublicFileIsOpenToAll()
    {
        var w = await BuildAsync();
        var publicUpload = await w.VictimClient.PostAsync("/api/v1/storage/upload", new MultipartFormDataContent
        {
            { new ByteArrayContent(TestFiles.Png()), "file", "logo.png" }, { new StringContent("true"), "isPublic" },
        });
        var publicId = (await ReadAsync(publicUpload)).GetProperty("file").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await w.AttackerClient.GetAsync($"/api/v1/storage/files/{w.FileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.AttackerClient.GetAsync($"/api/v1/storage/files/{publicId}")).StatusCode);    // public means public
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientWithKey(null).GetAsync($"/api/v1/storage/files/{publicId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.AttackerClient.DeleteAsync($"/api/v1/storage/files/{publicId}")).StatusCode);   // but not deletable by others
    }

    [Fact]
    public async Task WithoutAKey_EveryCallIs401()
    {
        var w = await BuildAsync();
        var anonymous = factory.ClientWithKey(null);
        var open = new List<string>();

        foreach (var (name, call) in CallsAtTheVictim(w))
        {
            var response = await call(anonymous);
            if (response.StatusCode != HttpStatusCode.Unauthorized) open.Add($"{name}: {(int)response.StatusCode}");
        }

        Assert.Empty(open);
    }
}
