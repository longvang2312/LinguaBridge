using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppInHouse.SharedServices.IntegrationTests;

public partial class WebTests : IClassFixture<WebFactory>
{
    private readonly WebFactory _factory;

    public WebTests(WebFactory factory)
    {
        _factory = factory;
        _factory.Api.Failure = null;
        _factory.Api.Calls.Clear();
        _factory.Api.Items =
        [
            WebFactory.Config("Main SMTP", 2, "Active"),
            WebFactory.Config("Main SMTP", 1, "Archived"),
            WebFactory.Config("Main SMTP", 3, "Draft"),
            WebFactory.Config("Alerts bot", 1, "Draft", project: "identity", channel: "Telegram", provider: "TelegramBot"),
            WebFactory.Config("Local uploads", 1, "Active", project: "default", channel: "Storage", provider: "LocalDisk", hasSecret: false),
        ];
    }

    // Cookies are kept (the anti-forgery cookie and TempData travel with them); redirects are followed by hand so they can be checked.
    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    [GeneratedRegex("""<form[^>]*method="post"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex PostForm();

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)"|value="([^"]+)"[^>]*name="__RequestVerificationToken" """, RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    [GeneratedRegex("""<form[^>]*action="([^"]*handler=Activate[^"]*)"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex ActivateAction();

    [GeneratedRegex("""<form[^>]*action="([^"]*handler=Rollback[^"]*)"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex RollbackAction();

    private static string TokenIn(string html)
    {
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*?value="([^"]+)""", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "no anti-forgery token on the page");
        return match.Groups[1].Value;
    }

    private static string Action(Regex regex, string html) => WebUtility.HtmlDecode(regex.Match(html).Groups[1].Value);

    // ------------------------------------------------------------------------------------------------ the shell

    [Fact]
    public async Task TheHomePage_ShowsTheBrandedShell_WithEveryAdministrationEntryInTheSidebar()
    {
        var response = await NewClient().GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("AppInHouse", html);
        Assert.Contains("Shared Services", html);
        Assert.Contains("bootstrap@5.3.3", html);               // Bootstrap 5
        Assert.Contains("class=\"topbar", html);                 // topbar
        Assert.Contains("id=\"sidebar\"", html);                 // sidebar
        Assert.Contains("Cấu hình Email", html);
        Assert.Contains("SMTP", html);
        Assert.Contains("SendGrid", html);
        Assert.Contains("Brevo", html);
        Assert.Contains("Tin Nhắn &amp; Chat", html);
        Assert.Contains("Telegram · Discord · Slack", html);
        Assert.Contains("Lưu trữ &amp; Media Browser", html);
        Assert.Contains("Ứng dụng &amp; API Keys", html);
        Assert.Contains("Audit Logs &amp; Lịch sử gửi tin", html);
    }

    [Fact]
    public async Task TheHomePage_CountsActiveAndDraftConfigurationsPerChannel()
    {
        var html = await NewClient().GetStringAsync("/");

        Assert.Contains("Email", html);
        Assert.Matches(@"(?s)Email.*?1\s*<small[^>]*>đang hoạt động.*?1 bản nháp", html);
    }

    [Theory]
    [InlineData("/ApiKeys", "Ứng dụng &amp; API Keys")]
    [InlineData("/Audit", "Audit Logs &amp; Lịch sử gửi tin")]
    public async Task TheSectionsStillToCome_ShowAClearPlaceholder(string path, string title)
    {
        var response = await NewClient().GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(title, html);
        Assert.Contains("Đang phát triển", html);
    }

    [Theory]
    [InlineData("/Storage", "Storage")]
    public async Task TheChannelEntries_OpenTheConfigurationListFilteredByThatChannel(string path, string channel)
    {
        var response = await NewClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/Configurations?channel={channel}", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task TheOlderTelegramAddress_GoesToTheChatPage()
    {
        var response = await NewClient().GetAsync("/Telegram");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Chat", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task AnUnknownPage_Is404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await NewClient().GetAsync("/does-not-exist")).StatusCode);

    // ------------------------------------------------------------------------------------------------ the configuration list

    [Fact]
    public async Task TheConfigurationList_ShowsProjectChannelProviderVersionAndStatus()
    {
        var response = await NewClient().GetAsync("/Configurations");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Danh sách cấu hình kênh", html);
        foreach (var header in new[] { "Dự án", "Kênh", "Provider", "Version", "Trạng thái", "Secret" })
            Assert.Contains(header, html);
        foreach (var cell in new[] { "linguabridge", "identity", "default", "Main SMTP", "Alerts bot", "Local uploads", "Smtp", "TelegramBot", "LocalDisk", "v2", "v3" })
            Assert.Contains(cell, html);
        Assert.Contains("text-bg-success\">Active", html);
        Assert.Contains("text-bg-warning\">Draft", html);
        Assert.Contains("text-bg-secondary\">Archived", html);
        Assert.Equal(5, Regex.Matches(html, "<tr data-version=").Count);
    }

    [Fact]
    public async Task Secrets_AreShownMasked_AndAConfigurationWithoutOneShowsADash()
    {
        var html = await NewClient().GetStringAsync("/Configurations");

        Assert.Equal(4, Regex.Matches(html, """<span class="secret-mask"[^>]*>••••••••</span>""").Count);   // the four rows that have a secret
        Assert.DoesNotContain("password", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheListCanBeFilteredByProjectAndByChannel()
    {
        var byChannel = await NewClient().GetStringAsync("/Configurations?channel=Telegram");
        var byProject = await NewClient().GetStringAsync("/Configurations?project=linguabridge");
        var both = await NewClient().GetStringAsync("/Configurations?project=linguabridge&channel=Storage");

        Assert.Contains("Cấu hình Telegram", byChannel);
        Assert.Single(Regex.Matches(byChannel, "<tr data-version="));
        Assert.Contains("Alerts bot", byChannel);
        Assert.Equal(3, Regex.Matches(byProject, "<tr data-version=").Count);
        Assert.DoesNotContain("Alerts bot", byProject);
        Assert.Empty(Regex.Matches(both, "<tr data-version="));
        Assert.Contains("Chưa có cấu hình nào", both);
    }

    [Fact]
    public async Task TheProjectFilter_OffersEveryProject_EvenWhenOneIsSelected()
    {
        var html = await NewClient().GetStringAsync("/Configurations?project=identity");

        Assert.Contains("<option value=\"default\"", html);
        Assert.Contains("<option value=\"identity\" selected", html);
        Assert.Contains("<option value=\"linguabridge\"", html);
    }

    [Fact]
    public async Task OnlyTheRightButtons_AreOffered_ForEachStatus()
    {
        var html = await NewClient().GetStringAsync("/Configurations");

        // Draft rows can be activated, archived rows rolled back to, the active row has neither.
        Assert.Equal(2, Regex.Matches(html, "Kích hoạt</button>").Count);   // the two drafts
        Assert.Single(Regex.Matches(html, "Rollback</button>"));            // the archived version
    }

    // ------------------------------------------------------------------------------------------------ CSRF protection

    [Fact]
    public async Task EveryPostForm_CarriesAnAntiForgeryToken()
    {
        var html = await NewClient().GetStringAsync("/Configurations");

        var forms = PostForm().Matches(html);
        Assert.NotEmpty(forms);
        // Each <form method="post"> must be followed by its own hidden token before the form ends.
        foreach (Match form in forms)
        {
            var rest = html[form.Index..];
            var end = rest.IndexOf("</form>", StringComparison.Ordinal);
            Assert.Contains("__RequestVerificationToken", rest[..end]);
        }
    }

    [Fact]
    public async Task APostWithoutTheToken_IsRefused_AndNothingIsDone()
    {
        var client = NewClient();
        var html = await client.GetStringAsync("/Configurations");
        var action = Action(ActivateAction(), html);

        var response = await client.PostAsync(action, new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("activate:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APostWithATokenButWithoutTheCookie_IsRefused()
    {
        var html = await NewClient().GetStringAsync("/Configurations");
        var token = TokenIn(html);
        var action = Action(ActivateAction(), html);

        var strangerWithoutCookies = NewClient();   // another browser: has no anti-forgery cookie
        var response = await strangerWithoutCookies.PostAsync(action, new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("activate:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APostWithAForgedToken_IsRefused()
    {
        var client = NewClient();
        var html = await client.GetStringAsync("/Configurations");
        var action = Action(ActivateAction(), html);

        var response = await client.PostAsync(action, new FormUrlEncodedContent([new("__RequestVerificationToken", "CfDJ8-forged-token")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheAntiForgeryCookie_IsHttpOnlyAndSameSiteStrict()
    {
        var response = await NewClient().GetAsync("/Configurations");

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("AppInHouse.SharedServices.Antiforgery", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------------------ actions

    [Fact]
    public async Task Activate_WithAValidToken_CallsTheApi_AndShowsTheResult()
    {
        var client = NewClient();
        var html = await client.GetStringAsync("/Configurations?project=linguabridge");
        var token = TokenIn(html);
        var action = Action(ActivateAction(), html);
        var draftId = Regex.Match(action, "id=([0-9a-f-]{36})").Groups[1].Value;

        var post = await client.PostAsync(action, new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains(_factory.Api.Calls, c => c == $"activate:{draftId}");
        var follow = await client.GetStringAsync(post.Headers.Location!.OriginalString);
        Assert.Contains("id=\"flash-message\"", follow);
        Assert.Contains("Đã kích hoạt Main SMTP phiên bản", follow);
    }

    [Fact]
    public async Task Rollback_WithAValidToken_AsksTheApiForTheChosenVersion()
    {
        var client = NewClient();
        var html = await client.GetStringAsync("/Configurations");
        var token = TokenIn(html);
        var action = Action(RollbackAction(), html);

        var post = await client.PostAsync(action, new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", token),
            new("projectCode", "linguabridge"),
            new("channelType", "Email"),
            new("name", "Main SMTP"),
            new("version", "1"),
        ]));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains("rollback:linguabridge:Email:Main SMTP:1", _factory.Api.Calls);
        Assert.Contains("Đã quay về Main SMTP phiên bản", await client.GetStringAsync(post.Headers.Location!.OriginalString));
    }

    [Fact]
    public async Task WhenTheApiRefusesAnAction_TheUserSeesWhy_NotAnErrorPage()
    {
        var client = NewClient();
        var html = await client.GetStringAsync("/Configurations");
        var token = TokenIn(html);
        var action = Action(ActivateAction(), html);
        _factory.Api.Failure = new SharedServicesApiException(409, "Version 3 is already active.");

        var post = await client.PostAsync(action, new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
        _factory.Api.Failure = null;

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var follow = await client.GetStringAsync(post.Headers.Location!.OriginalString);
        Assert.Contains("id=\"flash-error\"", follow);
        Assert.Contains("Version 3 is already active.", follow);
    }

    // ------------------------------------------------------------------------------------------------ the API is not available

    [Fact]
    public async Task WhenTheApiIsDown_ThePageStillOpens_AndSaysSo()
    {
        _factory.Api.Failure = new HttpRequestException("connection refused");

        var response = await NewClient().GetAsync("/Configurations");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"load-error\"", html);
        Assert.Contains("Không kết nối được Shared Services API", html);
        Assert.DoesNotContain("connection refused", html);   // the technical detail stays in the logs
        Assert.DoesNotContain("Chưa có cấu hình nào", html);
    }

    [Fact]
    public async Task WhenThePortalHasNoValidApiKey_TheMessageSaysWhichSettingToFix()
    {
        _factory.Api.Failure = new SharedServicesApiException(401, "The service did not accept the API key.");

        var html = await NewClient().GetStringAsync("/");

        Assert.Contains("SharedServices:Api:ApiKey", html);
    }
}
