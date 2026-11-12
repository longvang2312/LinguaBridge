using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using AppInHouse.SharedServices.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>
/// The conversation tables and queries, once on SQLite (development and tests) and once on PostgreSQL (production): the same checks, so
/// anything one provider translates differently from the other (the cursor over time and id, group counts, the unique pair) shows up.
/// </summary>
public class ConversationPersistenceTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    private static string NewProject() => "c" + Guid.NewGuid().ToString("N")[..8];

    private static async Task SaveAsync(PersistenceHost host, Action<SharedServicesDbContext> arrange)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        arrange(db);
        await db.SaveChangesAsync();
    }

    private static T Repo<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    // ------------------------------------------------------------------------------------------------ the unique pair

    private static async Task CheckUniquePairAsync(PersistenceHost host)
    {
        var project = NewProject();
        await SaveAsync(host, db => db.Conversations.Add(Conversation.CreateDirect(project, "lan", "minh", Day)));

        // The same two users, the other way round, in the same project: refused by the database, not only by the service.
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            db.Conversations.Add(Conversation.CreateDirect(project, "minh", "lan", Day));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Another project, another pair, and any number of groups (no key at all) are fine.
        await SaveAsync(host, db =>
        {
            db.Conversations.Add(Conversation.CreateDirect(NewProject(), "lan", "minh", Day));
            db.Conversations.Add(Conversation.CreateDirect(project, "lan", "an", Day));
            db.Conversations.Add(Conversation.CreateGroup(project, "One", "lan", Day));
            db.Conversations.Add(Conversation.CreateGroup(project, "Two", "lan", Day));
        });

        using var check = host.CreateScope();
        var found = await Repo<IConversationRepository>(check).FindDirectAsync(project, Conversation.DirectKeyOf("minh", "lan"));
        Assert.NotNull(found);
        Assert.Equal(["lan", "minh"], found.Members.Select(m => m.UserId).Order());
    }

    [Fact]
    public async Task Sqlite_TheSamePairHasOneDirectConversationPerProject() => await CheckUniquePairAsync(await PersistenceHost.CreateSqliteAsync());

    [PostgresFact]
    public async Task Postgres_TheSamePairHasOneDirectConversationPerProject()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        await CheckUniquePairAsync(host);
    }

    // ------------------------------------------------------------------------------------------------ the race for the same pair

    private static async Task CheckRaceAsync(PersistenceHost host)
    {
        var project = NewProject();

        // Eight requests, each in its own scope (its own database context), starting the conversation of the same two users at once:
        // one wins the unique index, the others must answer with the winner's conversation instead of failing.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            using var scope = host.CreateScope();
            var me = i % 2 == 0 ? "lan" : "minh";
            var other = i % 2 == 0 ? "minh" : "lan";
            return await Repo<IConversationService>(scope).CreateAsync(new ChatActor(project, me, me), new CreateConversationCommand(ConversationType.Direct, null, [new MemberSpec(other)]));
        })));

        Assert.Single(results.Select(r => r.Conversation.Id).Distinct());
        Assert.Equal(1, results.Count(r => r.Created));
        using var check = host.CreateScope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<SharedServicesDbContext>().Conversations.CountAsync(c => c.ProjectCode == project));
    }

    [Fact]
    public async Task Sqlite_EightRequestsForTheSamePairAtOnce_MakeOneConversation() => await CheckRaceAsync(await PersistenceHost.CreateSqliteAsync());

    [PostgresFact]
    public async Task Postgres_EightRequestsForTheSamePairAtOnce_MakeOneConversation()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        await CheckRaceAsync(host);
    }

    // ------------------------------------------------------------------------------------------------ the cursor

    private static async Task<List<ChatMessage>> ReadAllPagesAsync(PersistenceHost host, Guid conversation, int pageSize)
    {
        var all = new List<ChatMessage>();
        DateTimeOffset? before = null;
        Guid? beforeId = null;
        while (true)
        {
            using var scope = host.CreateScope();
            var page = await Repo<IConversationRepository>(scope).ListMessagesAsync(conversation, before, beforeId, pageSize);
            all.AddRange(page);
            if (page.Count < pageSize) return all;
            before = page[^1].CreatedAt;
            beforeId = page[^1].Id;
        }
    }

    private static async Task CheckCursorAsync(PersistenceHost host)
    {
        var project = NewProject();
        var room = Conversation.CreateDirect(project, "lan", "minh", Day);
        var sameInstant = Day.AddMinutes(1);
        await SaveAsync(host, db =>
        {
            db.Conversations.Add(room);
            for (var i = 0; i < 12; i++) db.ChatMessages.Add(ChatMessage.Create(room.Id, "lan", "Lan", $"older{i}", null, Day.AddSeconds(i).AddTicks(i * 10)));
            for (var i = 0; i < 25; i++) db.ChatMessages.Add(ChatMessage.Create(room.Id, "minh", "Minh", $"same{i}", null, sameInstant));   // one instant, many messages
            for (var i = 0; i < 6; i++) db.ChatMessages.Add(ChatMessage.Create(room.Id, "lan", "Lan", $"newer{i}", null, Day.AddMinutes(2).AddSeconds(i)));
        });

        foreach (var size in new[] { 1, 7, 10, 100 })
        {
            var all = await ReadAllPagesAsync(host, room.Id, size);

            Assert.Equal(43, all.Count);
            Assert.Equal(43, all.Select(m => m.Id).Distinct().Count());   // nothing repeated, nothing skipped, whatever the page size
            Assert.Equal(all.OrderByDescending(m => m.CreatedAt).Select(m => m.CreatedAt), all.Select(m => m.CreatedAt));   // newest first
            Assert.Equal("newer5", all[0].Content);
            Assert.Equal("older0", all[^1].Content);
        }

        // A cursor with the time only (no id) skips what shares that instant: it is the plain "older than" form.
        using var scope = host.CreateScope();
        var older = await Repo<IConversationRepository>(scope).ListMessagesAsync(room.Id, sameInstant, null, 100);
        Assert.Equal(12, older.Count);
    }

    [Fact]
    public async Task Sqlite_TheHistoryCursor_NeverSkipsOrRepeats_EvenForMessagesOfTheSameInstant() => await CheckCursorAsync(await PersistenceHost.CreateSqliteAsync());

    [PostgresFact]
    public async Task Postgres_TheHistoryCursor_NeverSkipsOrRepeats_EvenForMessagesOfTheSameInstant()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        await CheckCursorAsync(host);
    }

    // ------------------------------------------------------------------------------------------------ my conversations

    private static async Task CheckListAsync(PersistenceHost host)
    {
        var project = NewProject();
        var other = NewProject();
        var withMinh = Conversation.CreateDirect(project, "lan", "minh", Day);
        var team = Conversation.CreateGroup(project, "Team", "lan", Day);
        team.AddMember("an", ConversationRole.Member, Day);
        var notMine = Conversation.CreateDirect(project, "minh", "an", Day);
        var elsewhere = Conversation.CreateDirect(other, "lan", "minh", Day);
        await SaveAsync(host, db =>
        {
            db.Conversations.AddRange(withMinh, team, notMine, elsewhere);
            db.ChatMessages.AddRange(
                ChatMessage.Create(withMinh.Id, "minh", "Minh", "one", null, Day.AddMinutes(1)),
                ChatMessage.Create(withMinh.Id, "minh", "Minh", "two", null, Day.AddMinutes(2)),
                ChatMessage.Create(withMinh.Id, "lan", "Lan", "my reply", null, Day.AddMinutes(3)),
                ChatMessage.Create(withMinh.Id, "minh", "Minh", "three", null, Day.AddMinutes(4)),
                ChatMessage.Create(team.Id, "an", "An", "hello team", null, Day.AddMinutes(5)),
                ChatMessage.System(team.Id, "bao joined", Day.AddMinutes(6)),
                ChatMessage.Create(notMine.Id, "an", "An", "secret", null, Day.AddMinutes(7)),
                ChatMessage.Create(elsewhere.Id, "minh", "Minh", "other project", null, Day.AddMinutes(8)));
            withMinh.TouchLastMessage(Day.AddMinutes(4));
            team.TouchLastMessage(Day.AddMinutes(6));
            notMine.TouchLastMessage(Day.AddMinutes(7));
            elsewhere.TouchLastMessage(Day.AddMinutes(8));
        });

        using var scope = host.CreateScope();
        var rows = await Repo<IConversationRepository>(scope).ListForUserAsync(project, "lan", 50);

        Assert.Equal([team.Id, withMinh.Id], rows.Select(r => r.Conversation.Id));   // most recently active first, mine only
        Assert.Equal(("bao joined", 1), (rows[0].Last?.Content, rows[0].Unread));    // the newest line is shown, but the system line is not counted as unread
        Assert.Equal(("three", 3), (rows[1].Last?.Content, rows[1].Unread));         // minh wrote three, lan's own reply is not unread
        Assert.Equal(["an", "lan"], rows[0].Conversation.Members.Select(m => m.UserId).Order());
        Assert.Empty(await Repo<IConversationRepository>(scope).ListForUserAsync(project, "ghost", 50));
    }

    [Fact]
    public async Task Sqlite_TheList_HasTheLastMessageAndTheUnreadCount() => await CheckListAsync(await PersistenceHost.CreateSqliteAsync());

    [PostgresFact]
    public async Task Postgres_TheList_HasTheLastMessageAndTheUnreadCount()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        await CheckListAsync(host);
    }

    // ------------------------------------------------------------------------------------------------ members, totals, cascade

    private static async Task CheckMembersTotalsAndCascadeAsync(PersistenceHost host)
    {
        var project = NewProject();
        var room = Conversation.CreateGroup(project, "Team", "lan", Day);
        room.AddMember("minh", ConversationRole.Member, Day);
        room.AddMember("an", ConversationRole.Admin, Day);
        var direct = Conversation.CreateDirect(project, "lan", "bao", Day);
        var today = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        await SaveAsync(host, db =>
        {
            db.Conversations.AddRange(room, direct);
            db.ChatMessages.AddRange(
                ChatMessage.Create(room.Id, "lan", "Lan", "yesterday", null, today.AddTicks(-1)),         // the last instant of the day before: not counted
                ChatMessage.Create(room.Id, "lan", "Lan", "first instant", null, today),                  // counted
                ChatMessage.Create(direct.Id, "bao", "Bao", "last instant", null, today.AddDays(1).AddTicks(-1)),   // counted
                ChatMessage.Create(direct.Id, "bao", "Bao", "tomorrow", null, today.AddDays(1)),          // not counted
                ChatMessage.System(room.Id, "an joined", today.AddHours(1)));                             // the service's own line is not a message
        });

        using (var scope = host.CreateScope())
        {
            var repo = Repo<IConversationRepository>(scope);
            Assert.Equal(["an", "bao", "minh"], (await repo.ListCoMemberIdsAsync(project, "lan")).Order());
            Assert.Equal(["lan", "minh"], (await repo.ListCoMemberIdsAsync(project, "an")).Order());
            Assert.Empty(await repo.ListCoMemberIdsAsync(project, "ghost"));

            var totals = await repo.GetTotalsAsync(project, today, today.AddDays(1));
            Assert.Equal((2, 1, 1, 2), (totals.Conversations, totals.Direct, totals.Groups, totals.MessagesInRange));
            Assert.True((await repo.GetTotalsAsync(null, today, today.AddDays(1))).Conversations >= 2);   // every project

            var recent = await repo.ListRecentAsync(project, 10);
            Assert.Equal(2, recent.Count);
            var team = recent.Single(r => r.Id == room.Id);
            Assert.Equal(("Group", 3, 2), (team.Type, team.MemberCount, team.MessageCount));
        }

        // Taking a member out persists, and deleting the conversation takes its members and messages with it.
        using (var scope = host.CreateScope())
        {
            var repo = Repo<IConversationRepository>(scope);
            var loaded = await repo.GetAsync(project, room.Id);
            Assert.NotNull(loaded);
            var minh = loaded.FindMember("minh")!;
            loaded.Members.Remove(minh);
            repo.RemoveMember(minh);
            await Repo<IUnitOfWork>(scope).SaveChangesAsync();
        }

        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            Assert.Equal(["an", "lan"], await db.ConversationMembers.Where(m => m.ConversationId == room.Id).Select(m => m.UserId).OrderBy(u => u).ToListAsync());
            db.Conversations.Remove(await db.Conversations.SingleAsync(c => c.Id == room.Id));
            await db.SaveChangesAsync();
            Assert.Equal(0, await db.ConversationMembers.CountAsync(m => m.ConversationId == room.Id));
            Assert.Equal(0, await db.ChatMessages.CountAsync(m => m.ConversationId == room.Id));
            Assert.Equal(2, await db.ChatMessages.CountAsync(m => m.ConversationId == direct.Id));
        }
    }

    [Fact]
    public async Task Sqlite_MembersTotalsAndTheCascade() => await CheckMembersTotalsAndCascadeAsync(await PersistenceHost.CreateSqliteAsync());

    [PostgresFact]
    public async Task Postgres_MembersTotalsAndTheCascade()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        await CheckMembersTotalsAndCascadeAsync(host);
    }

    // ------------------------------------------------------------------------------------------------ small things

    private static async Task CheckRoundTripAsync(PersistenceHost host)
    {
        var project = NewProject();
        var room = Conversation.CreateDirect(project, "lan", "minh", Day);
        var message = ChatMessage.Create(room.Id, "lan", "Lan", "Chào 👋 \"quoted\" & <b>", Guid.NewGuid(), Day.AddTicks(1234560));
        room.FindMember("minh")!.SetProfile("Minh", "minh@example.com", "chat-1");
        room.FindMember("minh")!.SetMuted(true);
        room.FindMember("minh")!.MarkRead(Day.AddTicks(7654320));
        await SaveAsync(host, db =>
        {
            db.Conversations.Add(room);
            db.ChatMessages.Add(message);
            db.ChatMessages.Add(ChatMessage.System(room.Id, "system line", Day.AddMinutes(1)));
        });

        using var scope = host.CreateScope();
        var repo = Repo<IConversationRepository>(scope);
        var loaded = (await repo.GetAsync(project, room.Id))!;
        var minh = loaded.FindMember("minh")!;
        Assert.Equal(("Minh", "minh@example.com", "chat-1", true), (minh.DisplayName, minh.Email, minh.ChatId, minh.IsMuted));
        Assert.Equal(Day.AddTicks(7654320), minh.LastReadAt);
        var stored = (await repo.GetMessageAsync(message.Id))!;
        Assert.Equal((message.Content, message.AttachmentFileId, message.CreatedAt, ChatMessageType.Media), (stored.Content, stored.AttachmentFileId, stored.CreatedAt, stored.MessageType));
        Assert.Equal("Lan", (await repo.GetLatestUserMessageAsync(room.Id))!.SenderName);   // the system line is newer but is not a person's message
        Assert.Null(await repo.GetAsync("another-project", room.Id));
    }

    [Fact]
    public async Task Sqlite_TextTimesAndFlags_RoundTrip() => await CheckRoundTripAsync(await PersistenceHost.CreateSqliteAsync());

    [PostgresFact]
    public async Task Postgres_TextTimesAndFlags_RoundTrip()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        await CheckRoundTripAsync(host);
    }
}
