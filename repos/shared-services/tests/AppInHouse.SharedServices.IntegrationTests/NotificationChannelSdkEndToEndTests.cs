using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using static AppInHouse.SharedServices.IntegrationTests.Support.PlatformApi;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>The SDK against the real API: the notification channel methods, and the older chat methods that now reach the same code through the aliases.</summary>
public class NotificationChannelSdkEndToEndTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<SharedServicesClient> SdkAsync(string project) =>
        new(factory.ClientWithKey((await factory.CreateKeyForProjectAsync(project, null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite, ApiScopes.ChatSend)).PlainKey));

    [Fact]
    public async Task ABotChannel_IsManagedTestedAndUsed_ThroughTheTypedMethods()
    {
        var project = NewProject();
        var sdk = await SdkAsync(project);

        var draft = await sdk.SaveNotificationChannelAsync(new SaveNotificationChannelRequest(project, "Mock", "Alerts"));
        Assert.Equal(("Chat", "Draft"), (draft.ChannelType, draft.Status));
        var tested = await sdk.TestNotificationChannelAsync(new TestBotChannelRequest(ConfigurationId: draft.Id));
        Assert.True(tested.Success);
        Assert.True(tested.UsedSavedConfiguration);
        var active = await sdk.ActivateNotificationChannelAsync(draft.Id);
        Assert.Equal("Active", active.Status);

        var listed = await sdk.GetNotificationChannelsAsync(project);
        Assert.Equal(draft.Id, Assert.Single(listed).Id);
        Assert.Equal(draft.Id, (await sdk.GetNotificationChannelAsync(draft.Id)).Id);
        Assert.Empty(await sdk.GetNotificationChannelsAsync(project, kind: "email"));

        var sent = await sdk.SendNotificationChannelMessageAsync(new SendBotMessageRequest("Disk almost full"));
        Assert.Equal(("Sent", true, project), (sent.Status, sent.Success, sent.ConfigurationProject));
        var logs = await sdk.GetBotDeliveryLogsAsync(project);
        Assert.Contains(logs, l => l.Status == "Sent");

        var v2 = await sdk.SaveNotificationChannelAsync(new SaveNotificationChannelRequest(project, "Mock", "Alerts", Settings: System.Text.Json.JsonDocument.Parse("""{"defaultChatId":"-9"}""").RootElement));
        await sdk.ActivateNotificationChannelAsync(v2.Id);
        var back = await sdk.RollbackNotificationChannelAsync(new RollbackNotificationChannelRequest(project, "Alerts"));
        Assert.Equal((draft.Id, 1), (back.Id, back.Version));
    }

    [Fact]
    public async Task ARefusal_IsAnApiException_WithTheStatus()
    {
        var project = NewProject();
        var sdk = await SdkAsync(project);

        var noBot = await Assert.ThrowsAsync<SharedServicesApiException>(() => sdk.SendNotificationChannelMessageAsync(new SendBotMessageRequest("hi")));
        var foreign = await Assert.ThrowsAsync<SharedServicesApiException>(() => sdk.GetNotificationChannelsAsync(NewProject()));
        var storage = await Assert.ThrowsAsync<SharedServicesApiException>(() => sdk.SaveNotificationChannelAsync(new SaveNotificationChannelRequest(project, "Mock", "x", ChannelType: "Storage")));

        Assert.Equal([409, 403, 400], new[] { noBot, foreign, storage }.Select(e => e.StatusCode));
    }
}
