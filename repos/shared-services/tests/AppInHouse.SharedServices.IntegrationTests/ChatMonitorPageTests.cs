using System.Net;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using static AppInHouse.SharedServices.IntegrationTests.Support.ChatApi;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>The /Chat page (real-time chat: totals, who is online, the hub check), against a fake API: what it shows for what the API says.</summary>
public class ChatMonitorPageTests : IClassFixture<WebFactory>
{
    private readonly WebFactory _factory;

    public ChatMonitorPageTests(WebFactory factory)
    {
        _factory = factory;
        factory.Api.Failure = null;
        factory.Api.Calls.Clear();
        factory.Api.Items = [];
        factory.Api.ChatLogs = [];
        factory.Api.ChatMonitor = null;
        factory.Api.RealtimeResult = null;
    }

    private HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string> BodyAsync(HttpResponseMessage response) => WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task TheChatPage_ShowsTheTotals_WhoIsOnline_AndTheBusiestConversations_ButNoSettingsForm()
    {
        var html = await BodyAsync(await Browser().GetAsync("/Chat?project=crm"));

        Assert.Matches("id=\"metric-conversations\">3<", html);
        Assert.Contains("(2 1-1 · 1 nhóm)", html);
        Assert.Matches("id=\"metric-messages-today\">12<", html);
        Assert.Matches("id=\"metric-online\">2<", html);
        Assert.Matches("id=\"metric-connections\">3<", html);
        Assert.Contains("data-user=\"lan\"", html);
        Assert.Contains("data-user=\"minh\"", html);
        Assert.Contains("Team LinguaBridge", html);
        Assert.Matches("(?s)<tr data-conversation=[^>]*>.*?Team LinguaBridge.*?Group.*?>4<.*?>9<", html);
        Assert.DoesNotContain("id=\"chat-form\"", html);
        Assert.DoesNotContain("id=\"send-test-button\"", html);
        Assert.Contains("chat-monitor", _factory.Api.Calls);
        Assert.Equal("crm", _factory.Api.LastMonitorProject);
    }

    [Fact]
    public async Task AQuietProject_SaysSo()
    {
        _factory.Api.ChatMonitor = new ChatMonitorDto("crm", 0, 0, 0, 0, 0, 0, [], []);

        var html = await BodyAsync(await Browser().GetAsync("/Chat?project=crm"));

        Assert.Contains("id=\"no-online\"", html);
        Assert.Contains("id=\"no-conversations\"", html);
        Assert.Matches("id=\"metric-conversations\">0<", html);
    }

    [Fact]
    public async Task TheCheckButton_AsksTheHubOnlyWhenPressed_AndShowsWhatItFound()
    {
        var browser = Browser();

        var before = await BodyAsync(await browser.GetAsync("/Chat?project=crm"));
        Assert.DoesNotContain("realtime-check", string.Join(',', _factory.Api.Calls));
        Assert.DoesNotContain("id=\"realtime-result\"", before);
        Assert.Contains("id=\"check-realtime-button\"", before);

        var after = await BodyAsync(await browser.GetAsync("/Chat?project=crm&check=true"));

        Assert.Contains("realtime-check", _factory.Api.Calls);
        Assert.Contains("id=\"realtime-result\" data-reachable=\"true\"", after);
        Assert.Contains("The hub is reachable and offers WebSockets.", after);
        Assert.Contains("WebSockets, ServerSentEvents, LongPolling", after);
        Assert.Contains("alert-success", Regex.Match(after, "<span[^>]*id=\"realtime-result\"[^>]*>").Value);
    }

    [Fact]
    public async Task AnUnreachableHub_IsShownAsAFailure()
    {
        _factory.Api.RealtimeResult = new RealtimeCheckResult(false, "The API key of this portal may not use the chat (it needs the chat:converse scope).", [], 3, 403);

        var html = await BodyAsync(await Browser().GetAsync("/Chat?project=crm&check=true"));

        Assert.Contains("id=\"realtime-result\" data-reachable=\"false\"", html);
        Assert.Contains("chat:converse", html);
        Assert.Contains("alert-danger", Regex.Match(html, "<span[^>]*id=\"realtime-result\"[^>]*>").Value);
    }

    [Fact]
    public async Task WhenTheApiFails_TheTabSaysSo_AndStillOffersTheCheck()
    {
        _factory.Api.Failure = new SharedServicesApiException(503, "The service is down.");

        var response = await Browser().GetAsync("/Chat?project=crm");
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"monitor-error\"", html);
        Assert.Contains("id=\"check-realtime-button\"", html);
        Assert.DoesNotContain("id=\"monitor-metrics\"", html);
    }

    [Fact]
    public async Task AUserNameOrTitleWithMarkup_IsEncoded()
    {
        _factory.Api.ChatMonitor = new ChatMonitorDto("crm", 1, 0, 1, 0, 1, 1, ["<script>alert(1)</script>"],
            [new MonitoredConversationDto(Guid.NewGuid(), "crm", "Group", "<img src=x onerror=alert(2)>", 2, 1, DateTimeOffset.UtcNow)]);

        var raw = await (await Browser().GetAsync("/Chat?project=crm")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>alert(1)</script>", raw);
        Assert.DoesNotContain("<img src=x", raw);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", raw);
    }
}

/// <summary>The same page against the real API: the conversations that really exist, and the real hub negotiation.</summary>
public class ChatMonitorPageEndToEndTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HttpClient PortalFor(HttpClient api) =>
        new PortalOnRealApiFactory(api).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    [Fact]
    public async Task ThePage_ShowsTheConversationsThatReallyExist_AndWhoIsReallyOnline()
    {
        var project = PlatformApi.NewProject();
        var key = await ProjectKeyAsync(factory, project, ApiScopes.ConfigRead);
        var lan = User(factory, key, "lan");
        await CreateGroupAsync(lan, "Team E2E", "minh");
        await SendAsync(lan, IdOf(await CreateDirectAsync(lan, "an")), "text that must not appear");
        await using var online = await HubProbe.ConnectAsync(factory, key, "lan");
        var portal = PortalFor(factory.ClientWithKey(key));

        var html = WebUtility.HtmlDecode(await portal.GetStringAsync($"/Chat?project={project}"));

        Assert.Matches("id=\"metric-conversations\">2<", html);
        Assert.Matches("id=\"metric-messages-today\">1<", html);
        Assert.Matches("id=\"metric-online\">1<", html);
        Assert.Matches("id=\"metric-connections\">1<", html);
        Assert.Contains("data-user=\"lan\"", html);
        Assert.Contains("Team E2E", html);
        Assert.DoesNotContain("text that must not appear", html);
    }

    [Fact]
    public async Task TheCheck_ReachesTheRealHub_WithAKeyThatMayChat()
    {
        var key = await ProjectKeyAsync(factory, "default", ApiScopes.ConfigRead);
        var portal = PortalFor(factory.ClientWithKey(key));

        var html = WebUtility.HtmlDecode(await portal.GetStringAsync("/Chat?project=default&check=true"));

        Assert.Contains("id=\"realtime-result\" data-reachable=\"true\"", html);
        Assert.Contains("The hub is reachable and offers WebSockets.", html);
        Assert.Contains("WebSockets", html);
    }

    [Fact]
    public async Task TheCheck_ExplainsAKeyThatMayNotChat_AndCountsNobodyAsOnline()
    {
        var admin = await PlatformApi.AdminAsync(factory);   // may configure and send, but has no chat:converse
        var portal = PortalFor(admin);

        var html = WebUtility.HtmlDecode(await portal.GetStringAsync("/Chat?project=default&check=true"));

        Assert.Contains("id=\"realtime-result\" data-reachable=\"false\"", html);
        Assert.Contains("chat:converse", html);
        var monitor = factory.Services.GetService(typeof(AppInHouse.SharedServices.Application.Conversations.IUserPresenceTracker)) as AppInHouse.SharedServices.Application.Conversations.IUserPresenceTracker;
        Assert.DoesNotContain("portal-probe", monitor!.OnlineUsers(null));   // the check negotiates, it never connects
    }
}
