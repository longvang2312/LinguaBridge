using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>The portal wired to the real API (in memory) instead of a fake one, so the page and the service are tested together.</summary>
public sealed class PortalOnRealApiFactory(HttpClient api) : WebApplicationFactory<AppInHouse.SharedServices.Web.WebEntryPoint>
{
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "ss-web-e2e-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DataProtection:KeysPath", _keys);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISharedServicesClient>();
            services.AddSingleton<ISharedServicesClient>(new SharedServicesClient(api));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { if (Directory.Exists(_keys)) Directory.Delete(_keys, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Review finding on TASK-033 (Major): with a version already active, "Save draft" reloaded the form from the ACTIVE version, so
/// "Save and activate" (as the page itself suggested) made a version with the old settings and the new draft's secret. These tests walk
/// the real page against the real service, on a project that already has an active version, which the first tests did not.
/// </summary>
public class EmailPageEndToEndTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Mask = "••••••••";

    private static string NewProject() => "p" + Guid.NewGuid().ToString("N")[..8];

    private static object Smtp(int port, string host = "127.0.0.1") => new
    {
        host, port, useSsl = "None", username = "mailer", senderEmail = "noreply@example.com", senderName = "AppInHouse",
    };

    private static string Attribute(string html, string id, string attribute = "value")
    {
        var tag = Regex.Match(html, $"<(?:input|select)[^>]*id=\"{id}\"[^>]*>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(tag);
        var match = Regex.Match(tag, $"{attribute}=\"([^\"]*)\"");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string Selected(string html, string selectId)
    {
        var select = Regex.Match(html, $"<select[^>]*id=\"{selectId}\"[^>]*>(.*?)</select>", RegexOptions.Singleline).Groups[1].Value;
        return Regex.Match(select, "<option value=\"([^\"]*)\" selected").Groups[1].Value;
    }

    /// <summary>The form exactly as the browser would post it back, read from the page the user is looking at.</summary>
    private static List<KeyValuePair<string, string>> FormAsLoaded(string html, string project) =>
    [
        new("__RequestVerificationToken", Regex.Match(html, """name="__RequestVerificationToken"[^>]*?value="([^"]+)""").Groups[1].Value),
        new("Form.ProjectCode", project),
        new("Form.Name", Attribute(html, "Form_Name")),
        new("Form.ProviderType", Selected(html, "Form_ProviderType")),
        new("Form.SmtpHost", Attribute(html, "Form_SmtpHost")),
        new("Form.SmtpPort", Attribute(html, "Form_SmtpPort")),
        new("Form.SmtpSecurity", Selected(html, "Form_SmtpSecurity")),
        new("Form.SmtpUsername", Attribute(html, "Form_SmtpUsername")),
        new("Form.SenderEmail", Attribute(html, "Form_SenderEmail")),
        new("Form.SenderName", Attribute(html, "Form_SenderName")),
        new("Form.Secret", Attribute(html, "Form_Secret")),
    ];

    private static List<KeyValuePair<string, string>> With(List<KeyValuePair<string, string>> fields, params (string Key, string Value)[] changes)
    {
        var map = fields.ToDictionary(f => f.Key, f => f.Value);
        foreach (var (key, value) in changes) map[key] = value;
        return [.. map];
    }

    private async Task<HttpClient> AdminApiAsync() =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite, ApiScopes.EmailSend)).PlainKey);

    [Fact]
    public async Task SaveDraftThenActivate_ActivatesTheDraftsValues_NotTheOldSettingsWithTheNewSecret()
    {
        await using var oldServer = new FakeSmtpServer { Accept = ("mailer", "OLD-password-A") }.Start();
        await using var newServer = new FakeSmtpServer { Accept = ("mailer", "NEW-password-B") }.Start();
        var project = NewProject();
        var admin = await AdminApiAsync();

        // The project already has an ACTIVE version: old server, old password.
        var created = await admin.PostAsJsonAsync("/api/v1/channel-configurations", new
        {
            projectCode = project, channelType = "Email", providerType = "Smtp", name = "Default email", settings = Smtp(oldServer.Port), secret = "OLD-password-A",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var firstId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/channel-configurations/{firstId}/activate", null)).StatusCode);

        using var portal = new PortalOnRealApiFactory(admin);
        var browser = portal.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        // 1. Open the page: it shows the active version.
        var first = await browser.GetStringAsync($"/Email?project={project}");
        Assert.Equal(oldServer.Port.ToString(), Attribute(first, "Form_SmtpPort"));
        Assert.Equal(Mask, Attribute(first, "Form_Secret"));

        // 2. Change the server and type a new password, save as a draft.
        var draftPost = await browser.PostAsync("/Email?handler=SaveDraft", new FormUrlEncodedContent(With(FormAsLoaded(first, project),
            ("Form.SmtpPort", newServer.Port.ToString()), ("Form.Secret", "NEW-password-B"))));
        Assert.Equal(HttpStatusCode.Redirect, draftPost.StatusCode);

        // 3. The page reloads: it must show THE DRAFT that was just saved (new server), not the old active version.
        var reloaded = await browser.GetStringAsync(draftPost.Headers.Location!.OriginalString);
        Assert.Equal(newServer.Port.ToString(), Attribute(reloaded, "Form_SmtpPort"));
        Assert.Equal(Mask, Attribute(reloaded, "Form_Secret"));
        Assert.Contains("phiên bản <strong>v2</strong>", reloaded);
        Assert.Contains("Draft", reloaded);
        Assert.Contains("id=\"live-version\"", reloaded);                  // and says which version is live meanwhile
        Assert.Contains("Đang hoạt động: <strong>v1</strong>", reloaded);
        Assert.DoesNotContain("NEW-password-B", reloaded);

        // 4. Do what the page says: "Save and activate", posting the form as it was loaded.
        var activatePost = await browser.PostAsync("/Email?handler=SaveActivate", new FormUrlEncodedContent(FormAsLoaded(reloaded, project)));
        Assert.Equal(HttpStatusCode.Redirect, activatePost.StatusCode);

        // 5. What is live now has the draft's server AND the draft's password, together.
        using var scope = factory.Services.CreateScope();
        var runtime = await scope.ServiceProvider.GetRequiredService<AppInHouse.SharedServices.Application.Configurations.IChannelConfigManager>()
            .GetActiveForRuntimeAsync(project, ChannelType.Email);
        Assert.NotNull(runtime);
        Assert.Equal(newServer.Port, runtime.Settings.GetProperty("port").GetInt32());
        Assert.Equal("NEW-password-B", runtime.Secret);

        // ... so mail really goes out, through the new server, with the new password, and the old server is never touched again.
        var send = await admin.PostAsJsonAsync("/api/v1/emails/send", new { to = "check@example.com", projectCode = project, subject = "after", textBody = "after" });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal(("mailer", "NEW-password-B"), Assert.Single(newServer.Logins));
        Assert.Single(newServer.Mails);
        Assert.Empty(oldServer.Logins);
        Assert.Empty(oldServer.Mails);
    }

    [Fact]
    public async Task EveryVersionThatIsActivatedThroughThePage_HasSettingsAndSecretThatBelongTogether()
    {
        // Edit, save a draft, edit AGAIN on the reloaded page, save a second draft, activate: each step builds on what the page showed.
        await using var serverOne = new FakeSmtpServer { Accept = ("mailer", "pass-1") }.Start();
        await using var serverTwo = new FakeSmtpServer { Accept = ("mailer", "pass-2") }.Start();
        await using var serverThree = new FakeSmtpServer { Accept = ("mailer", "pass-3") }.Start();
        var project = NewProject();
        var admin = await AdminApiAsync();
        var firstId = (await (await admin.PostAsJsonAsync("/api/v1/channel-configurations", new
        {
            projectCode = project, channelType = "Email", providerType = "Smtp", name = "Default email", settings = Smtp(serverOne.Port), secret = "pass-1",
        })).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/v1/channel-configurations/{firstId}/activate", null);

        using var portal = new PortalOnRealApiFactory(admin);
        var browser = portal.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var page = await browser.GetStringAsync($"/Email?project={project}");
        var save1 = await browser.PostAsync("/Email?handler=SaveDraft", new FormUrlEncodedContent(With(FormAsLoaded(page, project), ("Form.SmtpPort", serverTwo.Port.ToString()), ("Form.Secret", "pass-2"))));
        page = await browser.GetStringAsync(save1.Headers.Location!.OriginalString);
        // A second edit on the reloaded page (it shows draft v2): the password stays the mask, the port changes again.
        var save2 = await browser.PostAsync("/Email?handler=SaveDraft", new FormUrlEncodedContent(With(FormAsLoaded(page, project), ("Form.SmtpPort", serverThree.Port.ToString()), ("Form.Secret", "pass-3"))));
        page = await browser.GetStringAsync(save2.Headers.Location!.OriginalString);
        Assert.Equal(serverThree.Port.ToString(), Attribute(page, "Form_SmtpPort"));
        Assert.Contains("phiên bản <strong>v3</strong>", page);

        await browser.PostAsync("/Email?handler=SaveActivate", new FormUrlEncodedContent(FormAsLoaded(page, project)));

        using var scope = factory.Services.CreateScope();
        var runtime = await scope.ServiceProvider.GetRequiredService<AppInHouse.SharedServices.Application.Configurations.IChannelConfigManager>()
            .GetActiveForRuntimeAsync(project, ChannelType.Email);
        Assert.Equal((serverThree.Port, "pass-3"), (runtime!.Settings.GetProperty("port").GetInt32(), runtime.Secret));
    }

    [Fact]
    public async Task ChangingOnlyTheSettings_OfAnActiveVersion_KeepsItsOwnPassword()
    {
        // The ordinary case must keep working: save a draft that changes a setting but not the password, then activate it.
        await using var server = new FakeSmtpServer { Accept = ("mailer", "unchanged-password") }.Start();
        var project = NewProject();
        var admin = await AdminApiAsync();
        var id = (await (await admin.PostAsJsonAsync("/api/v1/channel-configurations", new
        {
            projectCode = project, channelType = "Email", providerType = "Smtp", name = "Default email", settings = Smtp(server.Port), secret = "unchanged-password",
        })).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/v1/channel-configurations/{id}/activate", null);

        using var portal = new PortalOnRealApiFactory(admin);
        var browser = portal.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var page = await browser.GetStringAsync($"/Email?project={project}");

        var draft = await browser.PostAsync("/Email?handler=SaveDraft", new FormUrlEncodedContent(With(FormAsLoaded(page, project), ("Form.SenderName", "Renamed"))));
        page = await browser.GetStringAsync(draft.Headers.Location!.OriginalString);
        await browser.PostAsync("/Email?handler=SaveActivate", new FormUrlEncodedContent(FormAsLoaded(page, project)));

        using var scope = factory.Services.CreateScope();
        var runtime = await scope.ServiceProvider.GetRequiredService<AppInHouse.SharedServices.Application.Configurations.IChannelConfigManager>()
            .GetActiveForRuntimeAsync(project, ChannelType.Email);
        Assert.Equal(("Renamed", "unchanged-password", server.Port), (runtime!.Settings.GetProperty("senderName").GetString(), runtime.Secret, runtime.Settings.GetProperty("port").GetInt32()));
    }
}
