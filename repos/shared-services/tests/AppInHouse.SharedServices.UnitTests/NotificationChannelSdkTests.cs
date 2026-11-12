using System.Net;
using System.Text;
using System.Text.Json;
using AppInHouse.SharedServices.ClientSdk;

namespace AppInHouse.SharedServices.UnitTests;

/// <summary>The notification channel calls of the SDK: addresses, verbs and bodies (the older chat methods keep their own addresses).</summary>
public class NotificationChannelSdkTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (SharedServicesClient Client, StubHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new SharedServicesClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") }), handler);
    }

    private const string OneChannel = """
        {"id":"7f2d6a1e-0000-0000-0000-000000000001","projectCode":"crm","channelType":"Chat","providerType":"TelegramBot","name":"Alerts",
         "version":1,"status":"Draft","settings":{},"hasSecret":true,"maskedSecret":"••••••••","lastTestStatus":"Untested",
         "lastTestedAt":null,"activatedAt":null,"createdAt":"2026-11-12T08:00:00+00:00","updatedAt":"2026-11-12T08:00:00+00:00"}
        """;

    private static readonly Guid Id = Guid.Parse("7f2d6a1e-0000-0000-0000-000000000001");

    [Fact]
    public async Task TheList_AsksForTheKind_TheProject_AndTheStatus()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK, $"[{OneChannel}]"));

        var items = await client.GetNotificationChannelsAsync("crm", "all", "Active");
        await client.GetNotificationChannelsAsync();

        Assert.Equal("Alerts", Assert.Single(items).Name);
        Assert.Equal("/api/v1/notifications/channels?kind=all&projectCode=crm&status=Active", handler.Requests[0].Url);
        Assert.Equal("/api/v1/notifications/channels?kind=bot", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Save_Activate_Get_AndRollback_UseTheirOwnAddresses()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK, OneChannel));

        await client.SaveNotificationChannelAsync(new SaveNotificationChannelRequest("crm", "TelegramBot", "Alerts", Secret: "the-secret"));
        await client.ActivateNotificationChannelAsync(Id);
        await client.GetNotificationChannelAsync(Id);
        await client.RollbackNotificationChannelAsync(new RollbackNotificationChannelRequest("crm", "Alerts", TargetVersion: 1));

        Assert.Equal((HttpMethod.Post, "/api/v1/notifications/channels"), (handler.Requests[0].Method, handler.Requests[0].Url));
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(("TelegramBot", "Alerts", "the-secret"), (body.RootElement.GetProperty("providerType").GetString(), body.RootElement.GetProperty("name").GetString(), body.RootElement.GetProperty("secret").GetString()));
        Assert.Equal((HttpMethod.Post, $"/api/v1/notifications/channels/{Id}/activate"), (handler.Requests[1].Method, handler.Requests[1].Url));
        Assert.Equal((HttpMethod.Get, $"/api/v1/notifications/channels/{Id}"), (handler.Requests[2].Method, handler.Requests[2].Url));
        Assert.Equal((HttpMethod.Post, "/api/v1/notifications/channels/rollback"), (handler.Requests[3].Method, handler.Requests[3].Url));
    }

    [Fact]
    public async Task TestAndSend_GoToTheChannelAddresses()
    {
        var (client, handler) = Make(request => request.RequestUri!.AbsolutePath.EndsWith("/test", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"success":true,"message":"ok","latencyMs":3,"providerType":"Mock","usedSavedConfiguration":false,"configurationId":null}""")
            : Json(HttpStatusCode.OK, """{"success":true,"status":"Sent","deliveryLogId":null,"latencyMs":2,"configurationProject":"crm","configurationVersion":1}"""));

        var tested = await client.TestNotificationChannelAsync(new TestBotChannelRequest(ProviderType: "Mock"));
        var sent = await client.SendNotificationChannelMessageAsync(new SendBotMessageRequest("hello"));

        Assert.True(tested.Success);
        Assert.Equal("Sent", sent.Status);
        Assert.Equal(["/api/v1/notifications/channels/test", "/api/v1/notifications/channels/send"], handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task TheBotLogs_HaveTheirOwnAddress()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK, "[]"));

        await client.GetBotDeliveryLogsAsync("crm", 5);
        await client.GetBotDeliveryLogsAsync();

        Assert.Equal("/api/v1/notifications/channels/delivery-logs?limit=5&projectCode=crm", handler.Requests[0].Url);
        Assert.Equal("/api/v1/notifications/channels/delivery-logs?limit=20", handler.Requests[1].Url);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task ARefusal_ThrowsTheApiExceptionWithTheStatus(HttpStatusCode status)
    {
        var (client, _) = Make(_ => Json(status, """{"title":"refused","detail":"because"}"""));

        var ex = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.SendNotificationChannelMessageAsync(new SendBotMessageRequest("hi")));

        Assert.Equal(((int)status, "because"), (ex.StatusCode, ex.Message));
    }
}
