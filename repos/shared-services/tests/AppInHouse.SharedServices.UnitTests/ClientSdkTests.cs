using System.Net;
using System.Text;
using System.Text.Json;
using AppInHouse.SharedServices.ClientSdk;

namespace AppInHouse.SharedServices.UnitTests;

public class ClientSdkTests
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

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (SharedServicesClient Client, StubHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new SharedServicesClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") }), handler);
    }

    private const string OneConfiguration = """
        {"id":"7f2d6a1e-0000-0000-0000-000000000001","projectCode":"default","channelType":"Email","providerType":"Smtp","name":"Main",
         "version":2,"status":"Active","settings":{"host":"h"},"hasSecret":true,"maskedSecret":"••••••••","lastTestStatus":"Untested",
         "lastTestedAt":null,"activatedAt":null,"createdAt":"2026-11-11T08:00:00+00:00","updatedAt":"2026-11-11T08:00:00+00:00"}
        """;

    [Fact]
    public async Task List_BuildsTheQuery_AndReadsTheAnswer()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK, $"[{OneConfiguration}]"));

        var items = await client.ListConfigurationsAsync("default", "Email", "Active");

        var item = Assert.Single(items);
        Assert.Equal("Main", item.Name);
        Assert.Equal("••••••••", item.MaskedSecret);
        Assert.Equal("h", item.Settings.GetProperty("host").GetString());
        Assert.Equal("/api/v1/channel-configurations?projectCode=default&channelType=Email&status=Active", handler.Requests[0].Url);
    }

    [Fact]
    public async Task List_WithoutFilters_AsksForEverything()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK, "[]"));

        await client.ListConfigurationsAsync();

        Assert.Equal("/api/v1/channel-configurations", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Save_SendsTheSecretToTheApi_AsAWriteOnlyField()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.Created, OneConfiguration));
        using var settings = JsonDocument.Parse("""{"host":"h"}""");

        var saved = await client.SaveConfigurationAsync(new SaveChannelConfigurationRequest("default", "Email", "Smtp", "Main", settings.RootElement.Clone(), "typed-secret"));

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("\"secret\":\"typed-secret\"", handler.Requests[0].Body);
        Assert.Contains("\"projectCode\":\"default\"", handler.Requests[0].Body);
        Assert.Equal(2, saved.Version);   // and the answer carries no secret, only the mask
    }

    [Fact]
    public async Task Activate_AndRollback_UseTheirOwnPaths()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK, OneConfiguration));
        var id = Guid.Parse("7f2d6a1e-0000-0000-0000-000000000001");

        await client.ActivateConfigurationAsync(id);
        await client.RollbackConfigurationAsync(new RollbackConfigurationRequest("default", "Email", "Main", 1));

        Assert.Equal($"/api/v1/channel-configurations/{id}/activate", handler.Requests[0].Url);
        Assert.Equal("/api/v1/channel-configurations/rollback", handler.Requests[1].Url);
        Assert.Contains("\"targetVersion\":1", handler.Requests[1].Body);
    }

    [Fact]
    public async Task AValidationProblem_BecomesAnExceptionWithTheFieldErrors()
    {
        var (client, _) = Make(_ => Json(HttpStatusCode.BadRequest,
            """{"title":"One or more values are not valid.","status":400,"errors":{"SettingsJson":["'password' looks like a secret"]}}"""));

        var exception = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.GetConfigurationAsync(Guid.NewGuid()));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(["'password' looks like a secret"], exception.Errors["SettingsJson"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401, "API key")]
    [InlineData(HttpStatusCode.Forbidden, 403, "not allowed")]
    public async Task AnEmptyRefusal_GetsAReadableMessage(HttpStatusCode status, int code, string expected)
    {
        var (client, _) = Make(_ => new HttpResponseMessage(status));

        var exception = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.ListConfigurationsAsync());

        Assert.Equal(code, exception.StatusCode);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public async Task ANonJsonErrorPage_StillBecomesAnException()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") });

        var exception = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.ListConfigurationsAsync());

        Assert.Equal(502, exception.StatusCode);
    }

    [Fact]
    public async Task ProblemDetails_UseTheirDetailAsTheMessage()
    {
        var (client, _) = Make(_ => Json(HttpStatusCode.Conflict, """{"title":"conflict","detail":"Version 1 is already active."}"""));

        var exception = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.ActivateConfigurationAsync(Guid.NewGuid()));

        Assert.Equal("Version 1 is already active.", exception.Message);
    }
}
