using AppInHouse.SharedServices.Application.Notifications;
using AppInHouse.SharedServices.Application.Notifications.Bots;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure.Notifications.Bots;

namespace AppInHouse.SharedServices.UnitTests;

/// <summary>
/// The bots (Telegram, Discord, Slack) belong to the notification service, and "chat" means the real-time chat between users. These tests
/// keep that boundary from blurring again: where the bot types live, and what the domain says is a notification channel.
/// </summary>
public class BotOwnershipTests
{
    [Fact]
    public void TheBotTypes_LiveUnderTheNotificationService()
    {
        foreach (var type in new[] { typeof(INotificationBotDispatcher), typeof(NotificationBotDispatcher), typeof(IBotProvider), typeof(BotSettingsReader), typeof(SendBotMessageCommand), typeof(INotificationBotTestService) })
            Assert.Equal("AppInHouse.SharedServices.Application.Notifications.Bots", type.Namespace);

        foreach (var type in new[] { typeof(TelegramBotProvider), typeof(DiscordWebhookBotProvider), typeof(SlackWebhookBotProvider), typeof(MockBotProvider), typeof(BotOptions) })
            Assert.Equal("AppInHouse.SharedServices.Infrastructure.Notifications.Bots", type.Namespace);
    }

    [Fact]
    public void NoChatNamespaceIsLeftInTheApplicationLayer_TheRealtimeChatIsConversations()
    {
        var uses = typeof(ScopedNotificationChannelSender).Assembly.GetTypes()
            .Where(t => t.Namespace is not null && t.Namespace.EndsWith(".Chat", StringComparison.Ordinal));

        Assert.Empty(uses);   // no "Chat" namespace is left in the application layer: the real-time chat is "Conversations"
    }

    [Fact]
    public void NoTypeOfTheBotsIsNamedAfterChat_SoItCannotBeConfusedWithTheRealtimeChatEntities()
    {
        var domain = typeof(ChannelType).Assembly.GetTypes().Where(t => t.Name == "ChatMessage").ToList();
        var application = typeof(INotificationBotDispatcher).Assembly.GetTypes().Where(t => t.Name == "ChatMessage").ToList();

        Assert.Single(domain);       // the stored message of a conversation, nothing else
        Assert.Empty(application);   // the message a bot sends is a BotMessage
        Assert.Contains(typeof(BotMessage), typeof(INotificationBotDispatcher).Assembly.GetTypes());
    }

    [Fact]
    public void TheDomain_SaysWhichChannelsAreNotificationChannels()
    {
        Assert.Equal([ChannelType.Email, ChannelType.Chat, ChannelType.Telegram], ProviderCatalog.NotificationChannels);
        Assert.Equal([ChannelType.Chat, ChannelType.Telegram], ProviderCatalog.BotChannels);
        Assert.True(ProviderCatalog.IsNotificationChannel(ChannelType.Email));
        Assert.True(ProviderCatalog.IsNotificationChannel(ChannelType.Chat));
        Assert.True(ProviderCatalog.IsNotificationChannel(ChannelType.Telegram));
        Assert.False(ProviderCatalog.IsNotificationChannel(ChannelType.Storage));
        Assert.True(ProviderCatalog.IsBotChannel(ChannelType.Telegram));
        Assert.False(ProviderCatalog.IsBotChannel(ChannelType.Email));
    }

    [Fact]
    public void TheBotChannels_AreStillStoredUnderTheirOldNames_SoNoStoredConfigurationChanges()
    {
        // The enum values are what the database holds; renaming them would orphan every saved bot.
        Assert.Equal("Chat", ChannelType.Chat.ToString());
        Assert.Equal("Telegram", ChannelType.Telegram.ToString());
        Assert.Equal("Chat", NotificationChannel.Chat.ToString());
    }

    [Theory]
    [InlineData(ProviderType.TelegramBot)]
    [InlineData(ProviderType.DiscordWebhook)]
    [InlineData(ProviderType.SlackWebhook)]
    [InlineData(ProviderType.Mock)]
    public void TheBotProviders_ServeTheChatChannel(ProviderType provider) => Assert.True(ProviderCatalog.Supports(ChannelType.Chat, provider));
}
