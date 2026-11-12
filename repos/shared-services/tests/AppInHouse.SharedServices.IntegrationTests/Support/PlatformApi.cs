using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure.Notifications.Bots;
using AppInHouse.SharedServices.Application.Notifications.Bots;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.IntegrationTests.Support;

/// <summary>What the platform tests share: the clients, the configurations they need, waiting for the background workers.</summary>
internal static class PlatformApi
{
    public const string Configs = "/api/v1/channel-configurations";

    public static string NewProject() => "p" + Guid.NewGuid().ToString("N")[..8];

    public static string NewRecipient() => $"user-{Guid.NewGuid():N}@example.com";

    /// <summary>A key of the platform project that can do everything an administrator does.</summary>
    public static async Task<HttpClient> AdminAsync(ApiFactory factory) => factory.ClientWithKey((await factory.CreateKeyForProjectAsync(
        "default", null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite, ApiScopes.EmailSend, ApiScopes.ChatSend, ApiScopes.StorageUpload, ApiScopes.StorageRead, ApiScopes.StorageDelete)).PlainKey);

    public static async Task<HttpClient> ProjectClientAsync(ApiFactory factory, string project, params string[] scopes) =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync(project, null, scopes)).PlainKey);

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    /// <summary>Saves and activates a configuration of a channel (<c>Email</c>, <c>Chat</c> or <c>Storage</c>) through the API.</summary>
    public static async Task<JsonElement> ActivateAsync(HttpClient admin, string project, string channel, string provider, object? settings = null, string? secret = null, string name = "Main")
    {
        var response = await admin.PostAsJsonAsync(Configs, new { projectCode = project, channelType = channel, providerType = provider, name, settings = settings ?? new { }, secret });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await ReadAsync(response);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Configs}/{saved.GetProperty("id").GetGuid()}/activate", null)).StatusCode);
        return saved;
    }

    public static Task<JsonElement> ActivateMockEmailAsync(HttpClient admin, string project) =>
        ActivateAsync(admin, project, "Email", "Mock", new { senderEmail = "noreply@example.com" });

    public static Task<JsonElement> ActivateMockChatAsync(HttpClient admin, string project) => ActivateAsync(admin, project, "Chat", "Mock");

    /// <summary>An email configuration that sends through the stubbed SendGrid, so a test can make the provider fail by setting <c>factory.SendGrid.Respond</c>.</summary>
    public static Task<JsonElement> ActivateSendGridAsync(HttpClient admin, string project) =>
        ActivateAsync(admin, project, "Email", "SendGrid", new { senderEmail = "noreply@example.com" }, "SG-platform-tests-api-key-0001");

    /// <summary>
    /// The values of a notification's placeholders. The client's JSON naming would turn the property names of an anonymous object into
    /// camelCase ("userName"); the keys of a dictionary are sent as they are, and placeholders are case-sensitive.
    /// </summary>
    public static Dictionary<string, object?> Data(object values) => values.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(values));

    public static MockBotProvider MockChat(ApiFactory factory) => factory.Services.GetServices<IBotProvider>().OfType<MockBotProvider>().Single();

    public static HttpResponseMessage ServerError() => new(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"errors":[{"message":"provider is down"}]}""") };

    public static HttpResponseMessage Accepted() => new(HttpStatusCode.Accepted) { Headers = { { "X-Message-Id", "sg-platform-1" } } };

    /// <summary>Waits (at most <paramref name="seconds"/>) until the condition holds; the workers run in the background of the host.</summary>
    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }

        return await condition();
    }

    public static Task<bool> UntilAsync(Func<bool> condition, int seconds = 15) => UntilAsync(() => Task.FromResult(condition()), seconds);
}
