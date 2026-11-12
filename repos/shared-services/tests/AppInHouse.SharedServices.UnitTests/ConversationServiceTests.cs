using System.Text.Json;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure.Realtime;
using AppInHouse.SharedServices.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppInHouse.SharedServices.UnitTests;

public class ConversationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public FakeConversationRepository Repo { get; } = new();
        public FakeStoredFileRepository Files { get; } = new();
        public FakeUnitOfWork Uow { get; } = new();
        public UserPresenceTracker Presence { get; } = new();
        public RecordingRealtime Realtime { get; } = new();
        public FakeJobQueue Jobs { get; } = new();
        public ConversationSettings Settings { get; } = new();
        public FixedClock Clock { get; } = new(Now);

        public ConversationService Service(IJobQueue? jobs = null) =>
            new(Repo, Files, Uow, Presence, Realtime, jobs ?? Jobs, Settings, Clock, NullLogger<ConversationService>.Instance);
    }

    private static ChatActor Actor(string user, string project = "crm", bool email = true, bool bot = true) => new(project, user, user.ToUpperInvariant(), email, bot);

    private static MemberSpec Spec(string user, string? email = null, string? chatId = null) => new(user, user + " name", email, chatId);

    private static async Task<ConversationDto> DirectAsync(Fixture f, string a = "lan", string b = "minh", string project = "crm") =>
        (await f.Service().CreateAsync(Actor(a, project), new CreateConversationCommand(ConversationType.Direct, null, [Spec(b)]))).Conversation;

    private static async Task<ConversationDto> GroupAsync(Fixture f, string owner = "lan", string[]? members = null, string project = "crm") =>
        (await f.Service().CreateAsync(Actor(owner, project), new CreateConversationCommand(ConversationType.Group, "Team", [.. (members ?? ["minh", "an"]).Select(m => Spec(m))]))).Conversation;

    // ------------------------------------------------------------------------------------------------ creating

    [Fact]
    public async Task ADirectConversation_HasTheTwoUsers_AsMembers()
    {
        var f = new Fixture();

        var result = await f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Direct, null, [Spec("minh")]));

        Assert.True(result.Created);
        Assert.Equal(("Direct", "crm", null), (result.Conversation.Type, result.Conversation.ProjectCode, result.Conversation.Title));
        Assert.Equal(["lan", "minh"], result.Conversation.Members.Select(m => m.UserId).Order());
        Assert.All(result.Conversation.Members, m => Assert.Equal("Member", m.Role));
        Assert.Single(f.Repo.Conversations);
    }

    [Fact]
    public async Task TheSamePairInTheOtherOrder_GetsTheExistingConversation_NeverASecond()
    {
        var f = new Fixture();
        var first = await DirectAsync(f, "lan", "minh");

        var again = await f.Service().CreateAsync(Actor("minh"), new CreateConversationCommand(ConversationType.Direct, null, [Spec("lan")]));
        var sameOrder = await f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Direct, null, [Spec("minh")]));

        Assert.False(again.Created);
        Assert.False(sameOrder.Created);
        Assert.Equal(first.Id, again.Conversation.Id);
        Assert.Equal(first.Id, sameOrder.Conversation.Id);
        Assert.Single(f.Repo.Conversations);
    }

    [Fact]
    public async Task TheSamePairInAnotherProject_IsAnotherConversation()
    {
        var f = new Fixture();

        var crm = await DirectAsync(f, "lan", "minh", "crm");
        var other = await DirectAsync(f, "lan", "minh", "linguabridge");

        Assert.NotEqual(crm.Id, other.Id);
        Assert.Equal(2, f.Repo.Conversations.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ADirectConversation_NeedsExactlyOneOtherUser(int others)
    {
        var f = new Fixture();
        var members = Enumerable.Range(0, others).Select(i => Spec("u" + i)).ToList();

        var ex = await Assert.ThrowsAsync<ValidationException>(() => f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Direct, null, members)));

        Assert.Contains("Members", ex.Errors.Keys);
        Assert.Empty(f.Repo.Conversations);
    }

    [Fact]
    public async Task AConversationWithYourself_IsRefused()
    {
        var f = new Fixture();

        // The caller is dropped from the list, so nobody is left: the same "exactly one other" rule.
        await Assert.ThrowsAsync<ValidationException>(() => f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Direct, null, [Spec("lan")])));
    }

    [Fact]
    public async Task AGroup_NeedsATitle_TheCreatorOwnsIt_AndTheMembersAreDeduplicated()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ValidationException>(() => f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Group, "  ", [Spec("minh")])));

        var created = (await f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(
            ConversationType.Group, " Team ", [Spec("minh"), Spec("minh"), Spec("lan"), Spec("an")]))).Conversation;

        Assert.Equal("Team", created.Title);
        Assert.Equal(3, created.Members.Count);
        Assert.Equal("Owner", created.Members.Single(m => m.UserId == "lan").Role);
        Assert.Equal(["an", "minh"], created.Members.Where(m => m.Role == "Member").Select(m => m.UserId).Order());
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public async Task AUserIdThatIsNotInTheAllowedAlphabet_IsRefused(string bad)
    {
        var f = new Fixture();

        await Assert.ThrowsAsync<ValidationException>(() => f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Direct, null, [new MemberSpec(bad)])));
        await Assert.ThrowsAsync<ValidationException>(() => f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Group, "T", [new MemberSpec(new string('x', 101))])));
    }

    [Fact]
    public async Task AGroupOverTheMemberLimit_IsRefused()
    {
        var f = new Fixture();
        var members = Enumerable.Range(0, Conversation.MaxMembers).Select(i => Spec("u" + i)).ToList();   // plus the creator: one too many

        await Assert.ThrowsAsync<ValidationException>(() => f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Group, "Big", members)));
    }

    [Fact]
    public async Task TheContactOfAMember_IsKept_ButNeverShownToTheOthers()
    {
        var f = new Fixture();

        var created = await f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(
            ConversationType.Direct, null, [Spec("minh", "minh.secret@example.com", "chat-77")], new MemberSpec("lan", "Lan", "lan.secret@example.com")));

        var stored = f.Repo.Conversations.Single().FindMember("minh")!;
        Assert.Equal(("minh.secret@example.com", "chat-77"), (stored.Email, stored.ChatId));
        var json = JsonSerializer.Serialize(created.Conversation) + JsonSerializer.Serialize(await f.Service().ListAsync(Actor("lan")));
        Assert.DoesNotContain("secret@example.com", json);
        Assert.DoesNotContain("chat-77", json);
    }

    // ------------------------------------------------------------------------------------------------ who may do what

    [Fact]
    public async Task ANonMember_CannotReadSendMarkReadOrListPeers_AndNothingIsSaved()
    {
        var f = new Fixture();
        var room = await DirectAsync(f);
        var outsider = Actor("eve");
        await f.Service().SendAsync(Actor("lan"), room.Id, "hello", null);
        var saves = f.Uow.Saves;
        var messages = f.Repo.Messages.Count;

        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().GetAsync(outsider, room.Id));
        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().GetMessagesAsync(outsider, room.Id, null, null, null));
        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().SendAsync(outsider, room.Id, "hi", null));
        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().MarkReadAsync(outsider, room.Id));
        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().GetPeersAsync(outsider, room.Id));
        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().AnnounceTypingAsync(outsider, room.Id, true));
        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().RenameAsync(outsider, room.Id, "x"));
        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().AddMembersAsync(outsider, room.Id, [Spec("eve")]));

        Assert.Equal(saves, f.Uow.Saves);
        Assert.Equal(messages, f.Repo.Messages.Count);
        Assert.Empty(f.Realtime.Typings);
    }

    [Fact]
    public async Task AConversationOfAnotherProject_DoesNotExist_ForAMemberIdOfTheSameName()
    {
        var f = new Fixture();
        var room = await DirectAsync(f, "lan", "minh", "crm");

        // "lan" of another project is a different person: the conversation is simply not there.
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().GetMessagesAsync(Actor("lan", "linguabridge"), room.Id, null, null, null));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().SendAsync(Actor("lan", "linguabridge"), room.Id, "hi", null));
        Assert.Empty(await f.Service().ListAsync(Actor("lan", "linguabridge")));
    }

    [Fact]
    public async Task AnUnknownConversation_IsNotFound()
    {
        var f = new Fixture();

        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().SendAsync(Actor("lan"), Guid.NewGuid(), "hi", null));
    }

    // ------------------------------------------------------------------------------------------------ sending

    [Fact]
    public async Task ASentMessage_IsSaved_PushedToEveryMember_AndMarksTheConversationReadForTheSender()
    {
        var f = new Fixture();
        var room = await DirectAsync(f);
        f.Clock.Advance(TimeSpan.FromMinutes(5));

        var message = await f.Service().SendAsync(Actor("lan"), room.Id, "  Chào Minh  ", null);

        Assert.Equal(("lan", "LAN", "Text", "Chào Minh", null), (message.SenderId, message.SenderName, message.Type, message.Content, message.AttachmentFileId));
        Assert.Equal(Now.AddMinutes(5), message.CreatedAt);
        var stored = Assert.Single(f.Repo.Messages);
        Assert.Equal(message.Id, stored.Id);
        var conversation = f.Repo.Conversations.Single();
        Assert.Equal(Now.AddMinutes(5), conversation.LastMessageAt);
        Assert.Equal(Now.AddMinutes(5), conversation.FindMember("lan")!.LastReadAt);
        Assert.Equal(Now, conversation.FindMember("minh")!.LastReadAt);
        var push = Assert.Single(f.Realtime.Messages);
        Assert.Equal("crm", push.Project);
        Assert.Equal(["lan", "minh"], push.Users.Order());
        Assert.Equal(message, push.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyMessage_IsRefused(string content)
    {
        var f = new Fixture();
        var room = await DirectAsync(f);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => f.Service().SendAsync(Actor("lan"), room.Id, content, null));

        Assert.Contains("Content", ex.Errors.Keys);
        Assert.Empty(f.Repo.Messages);
        Assert.Empty(f.Realtime.Messages);
    }

    [Fact]
    public async Task AMessageOverTheLimit_IsRefused_OneAtTheLimitIsKept()
    {
        var f = new Fixture();
        var room = await DirectAsync(f);

        await Assert.ThrowsAsync<ValidationException>(() => f.Service().SendAsync(Actor("lan"), room.Id, new string('a', ChatMessage.MaxContentLength + 1), null));
        var kept = await f.Service().SendAsync(Actor("lan"), room.Id, new string('a', ChatMessage.MaxContentLength), null);

        Assert.Equal(ChatMessage.MaxContentLength, kept.Content.Length);
    }

    [Fact]
    public async Task AFile_CanBeAttached_ButOnlyOneOfTheSameProject()
    {
        var f = new Fixture();
        var room = await DirectAsync(f);
        var mine = StoredFile.Create("crm", Guid.NewGuid(), ProviderType.LocalDisk, "a.png", "image/png", 10, new string('a', 64), false, Now);
        var foreign = StoredFile.Create("linguabridge", Guid.NewGuid(), ProviderType.LocalDisk, "b.png", "image/png", 10, new string('b', 64), false, Now);
        f.Files.Rows.AddRange([mine, foreign]);

        var ok = await f.Service().SendAsync(Actor("lan"), room.Id, string.Empty, mine.Id);
        var other = await Assert.ThrowsAsync<ValidationException>(() => f.Service().SendAsync(Actor("lan"), room.Id, "see", foreign.Id));
        var missing = await Assert.ThrowsAsync<ValidationException>(() => f.Service().SendAsync(Actor("lan"), room.Id, "see", Guid.NewGuid()));

        Assert.Equal(("Media", mine.Id), (ok.Type, ok.AttachmentFileId));
        Assert.Contains("AttachmentFileId", other.Errors.Keys);
        Assert.Contains("AttachmentFileId", missing.Errors.Keys);
        Assert.Single(f.Repo.Messages);
    }

    [Fact]
    public async Task WhenThePushOrTheQueueFails_TheMessageIsStillSavedAndReturned()
    {
        var f = new Fixture();
        var created = await f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Direct, null, [Spec("minh", "minh@example.com")]));
        f.Realtime.Fail = true;

        var message = await f.Service(new ThrowingJobQueue()).SendAsync(Actor("lan"), created.Conversation.Id, "still here", null);

        Assert.Equal("still here", message.Content);
        Assert.Equal(message.Id, Assert.Single(f.Repo.Messages).Id);
    }

    // ------------------------------------------------------------------------------------------------ history

    [Fact]
    public async Task TheHistory_IsNewestFirst_30APage_AndEveryPageContinuesWhereTheLastStopped()
    {
        var f = new Fixture();
        var room = await DirectAsync(f);
        for (var i = 0; i < 70; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            await f.Service().SendAsync(Actor("lan"), room.Id, $"m{i:D2}", null);
        }

        var seen = new List<string>();
        MessagePageDto page = await f.Service().GetMessagesAsync(Actor("minh"), room.Id, null, null, null);
        var sizes = new List<int>();
        while (true)
        {
            sizes.Add(page.Items.Count);
            seen.AddRange(page.Items.Select(m => m.Content));
            if (!page.HasMore) break;
            page = await f.Service().GetMessagesAsync(Actor("minh"), room.Id, page.NextBefore, page.NextBeforeId, null);
        }

        Assert.Equal([30, 30, 10], sizes);
        Assert.Equal(Enumerable.Range(0, 70).Reverse().Select(i => $"m{i:D2}"), seen);
    }

    [Fact]
    public async Task MessagesFromTheSameInstant_AreNeitherSkippedNorRepeated_WhenPaging()
    {
        var f = new Fixture();
        var room = await DirectAsync(f);
        for (var i = 0; i < 25; i++) await f.Service().SendAsync(Actor("lan"), room.Id, $"same{i}", null);   // the clock never moves

        var all = new List<Guid>();
        var page = await f.Service().GetMessagesAsync(Actor("lan"), room.Id, null, null, 10);
        while (true)
        {
            all.AddRange(page.Items.Select(m => m.Id));
            if (!page.HasMore) break;
            page = await f.Service().GetMessagesAsync(Actor("lan"), room.Id, page.NextBefore, page.NextBeforeId, 10);
        }

        Assert.Equal(25, all.Count);
        Assert.Equal(25, all.Distinct().Count());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(5, 5)]
    [InlineData(100, 100)]
    [InlineData(5000, 100)]
    public async Task ThePageSize_IsKeptBetweenOneAndAHundred(int asked, int expected)
    {
        var f = new Fixture();
        var room = await DirectAsync(f);
        for (var i = 0; i < 120; i++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            await f.Service().SendAsync(Actor("lan"), room.Id, "m" + i, null);
        }

        var page = await f.Service().GetMessagesAsync(Actor("lan"), room.Id, null, null, asked);

        Assert.Equal(expected, page.Items.Count);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task TheLastPage_HasNoCursor()
    {
        var f = new Fixture();
        var room = await DirectAsync(f);
        await f.Service().SendAsync(Actor("lan"), room.Id, "only", null);

        var page = await f.Service().GetMessagesAsync(Actor("lan"), room.Id, null, null, null);

        Assert.Single(page.Items);
        Assert.False(page.HasMore);
        Assert.Null(page.NextBefore);
        Assert.Null(page.NextBeforeId);
    }

    // ------------------------------------------------------------------------------------------------ my conversations, reading

    [Fact]
    public async Task TheList_ShowsMyConversationsOnly_WithTheLastMessageAndTheUnreadCount_MostRecentFirst()
    {
        var f = new Fixture();
        var withMinh = await DirectAsync(f, "lan", "minh");
        var withAn = await DirectAsync(f, "lan", "an");
        await DirectAsync(f, "minh", "an");   // not mine
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Service().SendAsync(Actor("minh"), withMinh.Id, "one", null);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Service().SendAsync(Actor("minh"), withMinh.Id, "two", null);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Service().SendAsync(Actor("an"), withAn.Id, "hello", null);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await f.Service().SendAsync(Actor("lan"), withAn.Id, "my own reply", null);   // never counts as unread for me

        var list = await f.Service().ListAsync(Actor("lan"));

        Assert.Equal([withAn.Id, withMinh.Id], list.Select(c => c.Id));
        Assert.Equal(("my own reply", 0), (list[0].LastMessage!.Content, list[0].UnreadCount));
        Assert.Equal(("two", 2), (list[1].LastMessage!.Content, list[1].UnreadCount));
    }

    [Fact]
    public async Task MarkingRead_ClearsTheUnreadCount_AndTellsTheMembers_OnlyWhenItMoved()
    {
        var f = new Fixture();
        var room = await DirectAsync(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Service().SendAsync(Actor("minh"), room.Id, "hello", null);
        Assert.Equal(1, (await f.Service().ListAsync(Actor("lan")))[0].UnreadCount);
        f.Clock.Advance(TimeSpan.FromMinutes(1));

        var readAt = await f.Service().MarkReadAsync(Actor("lan"), room.Id);
        var pushes = f.Realtime.Reads.Count;
        var again = await f.Service().MarkReadAsync(Actor("lan"), room.Id);   // the clock did not move: nothing new was read

        Assert.Equal(Now.AddMinutes(2), readAt);
        Assert.Equal(readAt, again);
        Assert.Equal(0, (await f.Service().ListAsync(Actor("lan")))[0].UnreadCount);
        Assert.Equal(1, pushes);
        var read = Assert.Single(f.Realtime.Reads);   // the second call moved nothing, so it pushed nothing
        Assert.Equal((room.Id, "lan", readAt), (read.Conversation, read.User, read.At));
        Assert.Equal(["lan", "minh"], read.Users.Order());
    }

    [Fact]
    public async Task TheReadTime_NeverMovesBack()
    {
        var f = new Fixture();
        var room = await DirectAsync(f);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        var late = await f.Service().MarkReadAsync(Actor("lan"), room.Id);
        f.Clock.Advance(TimeSpan.FromMinutes(-9));   // a clock that stepped back

        var after = await f.Service().MarkReadAsync(Actor("lan"), room.Id);

        Assert.Equal(late, after);
    }

    // ------------------------------------------------------------------------------------------------ members and roles

    [Fact]
    public async Task OnlyTheOwnerOrAnAdmin_AddsMembers_AndAJoinIsAnnouncedInTheGroup()
    {
        var f = new Fixture();
        var room = await GroupAsync(f);
        f.Repo.Conversations.Single().FindMember("an")!.SetRole(ConversationRole.Admin);

        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().AddMembersAsync(Actor("minh"), room.Id, [Spec("bao")]));
        var byAdmin = await f.Service().AddMembersAsync(Actor("an"), room.Id, [Spec("bao")]);
        var byOwner = await f.Service().AddMembersAsync(Actor("lan"), room.Id, [Spec("chi")]);

        Assert.Contains(byAdmin.Members, m => m.UserId == "bao" && m.Role == "Member");
        Assert.Equal(5, byOwner.Members.Count);
        Assert.Contains(f.Repo.Messages, m => m.MessageType == ChatMessageType.System && m.Content.Contains("bao"));
        Assert.Contains(f.Realtime.Changes, c => c.Users.Contains("chi"));
    }

    [Fact]
    public async Task AddingSomeoneWhoIsAlreadyIn_ChangesNothing_AndAMemberCannotBeAddedToADirectConversation()
    {
        var f = new Fixture();
        var group = await GroupAsync(f);
        var direct = await DirectAsync(f, "a1", "a2");
        var messages = f.Repo.Messages.Count;

        var again = await f.Service().AddMembersAsync(Actor("lan"), group.Id, [Spec("minh")]);
        await Assert.ThrowsAsync<ValidationException>(() => f.Service().AddMembersAsync(Actor("a1"), direct.Id, [Spec("a3")]));

        Assert.Equal(3, again.Members.Count);
        Assert.Equal(messages, f.Repo.Messages.Count);
    }

    [Fact]
    public async Task AMember_CanLeave_AndAnAnnouncementIsMade()
    {
        var f = new Fixture();
        var room = await GroupAsync(f);

        await f.Service().RemoveMemberAsync(Actor("minh"), room.Id, "minh");

        Assert.Null(f.Repo.Conversations.Single().FindMember("minh"));
        Assert.Contains(f.Repo.Messages, m => m.MessageType == ChatMessageType.System && m.Content.Contains("rời nhóm"));
        var change = f.Realtime.Changes[^1];        // the first one was the creation of the group
        Assert.Contains("minh", change.Users);      // the one who left is told too
        Assert.DoesNotContain("minh", f.Repo.Conversations.Single().Members.Select(m => m.UserId));
        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().GetMessagesAsync(Actor("minh"), room.Id, null, null, null));
    }

    [Fact]
    public async Task AMemberCannotRemoveSomeoneElse_AnAdminRemovesMembersOnly_TheOwnerRemovesAnyone()
    {
        var f = new Fixture();
        var room = await GroupAsync(f, members: ["admin", "member1", "member2", "admin2"]);
        var stored = f.Repo.Conversations.Single();
        stored.FindMember("admin")!.SetRole(ConversationRole.Admin);
        stored.FindMember("admin2")!.SetRole(ConversationRole.Admin);

        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().RemoveMemberAsync(Actor("member1"), room.Id, "member2"));
        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().RemoveMemberAsync(Actor("admin"), room.Id, "lan"));       // the owner
        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().RemoveMemberAsync(Actor("admin"), room.Id, "admin2"));    // another admin
        await f.Service().RemoveMemberAsync(Actor("admin"), room.Id, "member2");
        await f.Service().RemoveMemberAsync(Actor("lan"), room.Id, "admin2");

        Assert.Equal(["admin", "lan", "member1"], stored.Members.Select(m => m.UserId).Order());
    }

    [Fact]
    public async Task WhenTheOwnerLeaves_AnAdminTakesOver_OtherwiseTheOldestMember()
    {
        var f = new Fixture();
        var room = await GroupAsync(f, members: ["first", "second", "third"]);
        var stored = f.Repo.Conversations.Single();
        stored.FindMember("third")!.SetRole(ConversationRole.Admin);

        await f.Service().RemoveMemberAsync(Actor("lan"), room.Id, "lan");
        Assert.Equal(ConversationRole.Owner, stored.FindMember("third")!.Role);

        await f.Service().RemoveMemberAsync(Actor("third"), room.Id, "third");
        Assert.Single(stored.Members, m => m.Role == ConversationRole.Owner);
        Assert.Equal(2, stored.Members.Count);
    }

    [Fact]
    public async Task ADirectConversation_CannotBeLeftOrChanged_AndAnUnknownMemberIsNotFound()
    {
        var f = new Fixture();
        var direct = await DirectAsync(f);
        var group = await GroupAsync(f);

        await Assert.ThrowsAsync<ValidationException>(() => f.Service().RemoveMemberAsync(Actor("lan"), direct.Id, "lan"));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().RemoveMemberAsync(Actor("lan"), group.Id, "ghost"));
        Assert.Equal(2, f.Repo.Conversations[0].Members.Count);
    }

    [Fact]
    public async Task OnlyTheOwnerOrAnAdmin_RenamesAGroup()
    {
        var f = new Fixture();
        var room = await GroupAsync(f);
        var direct = await DirectAsync(f, "a1", "a2");

        await Assert.ThrowsAsync<ConversationAccessDeniedException>(() => f.Service().RenameAsync(Actor("minh"), room.Id, "Mine now"));
        var renamed = await f.Service().RenameAsync(Actor("lan"), room.Id, "  New name ");
        await Assert.ThrowsAsync<ValidationException>(() => f.Service().RenameAsync(Actor("lan"), room.Id, " "));
        await Assert.ThrowsAsync<ValidationException>(() => f.Service().RenameAsync(Actor("a1"), direct.Id, "Title"));

        Assert.Equal("New name", renamed.Title);
        Assert.Contains(f.Realtime.Changes, c => c.Conversation == room.Id);
    }

    // ------------------------------------------------------------------------------------------------ typing and presence

    [Fact]
    public async Task Typing_IsShownToTheOtherMembersOnly()
    {
        var f = new Fixture();
        var room = await GroupAsync(f);

        await f.Service().AnnounceTypingAsync(Actor("minh"), room.Id, true);

        var typing = Assert.Single(f.Realtime.Typings);
        Assert.Equal(["an", "lan"], typing.Users.Order());
        Assert.Equal(("minh", true), (typing.User, typing.Typing));
    }

    [Fact]
    public async Task APresenceChange_GoesToThePeersOfTheUser_AndTheOnlinePeersAreFound()
    {
        var f = new Fixture();
        await DirectAsync(f, "lan", "minh");
        await DirectAsync(f, "lan", "an");
        await DirectAsync(f, "minh", "bao");   // lan shares nothing with bao
        f.Presence.Connected("crm", "minh", "c1");
        f.Presence.Connected("crm", "bao", "c2");

        await f.Service().AnnouncePresenceAsync("crm", "lan", true);
        var online = await f.Service().GetOnlinePeersAsync(Actor("lan"));
        await f.Service().AnnouncePresenceAsync("crm", "nobody", true);   // no conversations: nobody to tell

        var change = Assert.Single(f.Realtime.Presences);
        Assert.Equal(["an", "minh"], change.Users.Order());
        Assert.Equal(("lan", true), (change.User, change.Online));
        Assert.Equal(["minh"], online);
    }

    // ------------------------------------------------------------------------------------------------ the offline notice

    private static (string Project, Guid Conversation, Guid Message, string Recipient) Notice(FakeJobQueue.Enqueued job)
    {
        var payload = Assert.IsType<OfflineNotificationPayload>(job.Payload);
        return (payload.Project, payload.ConversationId, payload.MessageId, payload.RecipientId);
    }

    [Fact]
    public async Task TheNotice_RemembersWhatTheSenderMaySend_AndIsNotQueuedWhenThereIsNothingToUse()
    {
        var f = new Fixture();
        var both = (await f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Direct, null, [Spec("minh", "minh@example.com", "chat-9")]))).Conversation;
        var onlyChatId = (await f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Direct, null, [Spec("an", null, "chat-1")]))).Conversation;

        await f.Service().SendAsync(Actor("lan", email: true, bot: false), both.Id, "one", null);
        await f.Service().SendAsync(Actor("lan", email: false, bot: false), both.Id, "two", null);          // nothing allowed: nothing queued
        await f.Service().SendAsync(Actor("lan", email: true, bot: false), onlyChatId.Id, "three", null);   // only a chat id, and bots are not allowed

        var job = Assert.Single(f.Jobs.Jobs);
        var payload = Assert.IsType<OfflineNotificationPayload>(job.Payload);
        Assert.Equal(("minh", true, false), (payload.RecipientId, payload.AllowEmail, payload.AllowBot));
    }

    [Fact]
    public async Task AMessageToAnOfflineMember_QueuesANoticeInTheChatQueue_AfterTheGracePeriod()
    {
        var f = new Fixture();
        var created = (await f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Direct, null, [Spec("minh", "minh@example.com")]))).Conversation;

        var message = await f.Service().SendAsync(Actor("lan"), created.Id, "are you there?", null);

        var job = Assert.Single(f.Jobs.Jobs);
        Assert.Equal((JobQueues.Chat, ConversationService.OfflineJobType, TimeSpan.FromSeconds(20)), (job.Queue, job.Type, job.Delay ?? TimeSpan.Zero));
        Assert.Equal(("crm", created.Id, message.Id, "minh"), Notice(job));
    }

    [Fact]
    public async Task TheGracePeriod_IsConfigurable()
    {
        var f = new Fixture();
        f.Settings.OfflineGraceSeconds = 45;
        var created = (await f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(ConversationType.Direct, null, [Spec("minh", "minh@example.com")]))).Conversation;

        await f.Service().SendAsync(Actor("lan"), created.Id, "hi", null);

        Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(f.Jobs.Jobs).Delay);
    }

    [Fact]
    public async Task NoNoticeIsQueued_ForTheSender_AnOnlineMember_AMutedOne_OrOneWithoutAnyContact()
    {
        var f = new Fixture();
        var created = (await f.Service().CreateAsync(Actor("lan"), new CreateConversationCommand(
            ConversationType.Group, "Team", [Spec("online", "online@example.com"), Spec("muted", "muted@example.com"), Spec("nocontact"), Spec("chatonly", chatId: "chat-1")],
            new MemberSpec("lan", "Lan", "lan@example.com")))).Conversation;
        f.Presence.Connected("crm", "online", "c1");
        f.Repo.Conversations.Single().FindMember("muted")!.SetMuted(true);

        await f.Service().SendAsync(Actor("lan"), created.Id, "hello team", null);

        Assert.Equal(["chatonly"], f.Jobs.Jobs.Select(j => Notice(j).Recipient));   // reachable only by chat: still reachable
    }

    [Fact]
    public async Task ASystemMessage_NeverQueuesANotice()
    {
        var f = new Fixture();
        var room = await GroupAsync(f);
        f.Repo.Conversations.Single().FindMember("minh")!.SetProfile(null, "minh@example.com", null);

        await f.Service().AddMembersAsync(Actor("lan"), room.Id, [Spec("bao", "bao@example.com")]);
        await f.Service().RemoveMemberAsync(Actor("bao"), room.Id, "bao");

        Assert.Empty(f.Jobs.Jobs);
    }
}

public class ChatDomainRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheDirectKey_IsTheSame_InEitherOrder()
    {
        Assert.Equal(Conversation.DirectKeyOf("lan", "minh"), Conversation.DirectKeyOf("minh", "lan"));
        Assert.NotEqual(Conversation.DirectKeyOf("lan", "minh"), Conversation.DirectKeyOf("lan", "mink"));
        Assert.Equal(Conversation.DirectKeyOf("a", "b"), Conversation.CreateDirect("p", "b", "a", Now).DirectKey);
    }

    [Fact]
    public void ADirectConversation_NeedsTwoDifferentValidUsers()
    {
        Assert.Throws<DomainException>(() => Conversation.CreateDirect("p", "lan", "lan", Now));
        Assert.Throws<DomainException>(() => Conversation.CreateDirect("p", "lan", "bad id", Now));
        Assert.Throws<DomainException>(() => Conversation.CreateDirect("p", "", "minh", Now));
    }

    [Theory]
    [InlineData("lan", true)]
    [InlineData("user_01.a@b:c-d", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("a b", false)]
    [InlineData("a|b", false)]
    [InlineData("tiếng-việt", false)]
    public void AUserId_UsesASafeAlphabet(string id, bool valid) => Assert.Equal(valid, ChatIdentity.IsValidUserId(id));

    [Fact]
    public void AUserId_IsAtMost100Characters()
    {
        Assert.True(ChatIdentity.IsValidUserId(new string('a', 100)));
        Assert.False(ChatIdentity.IsValidUserId(new string('a', 101)));
    }

    [Fact]
    public void AGroupTitle_IsRequired_AndAtMost200Characters()
    {
        Assert.Throws<DomainException>(() => Conversation.CreateGroup("p", " ", "lan", Now));
        Assert.Throws<DomainException>(() => Conversation.CreateGroup("p", new string('t', 201), "lan", Now));
        Assert.Equal(200, Conversation.CreateGroup("p", new string('t', 200), "lan", Now).Title!.Length);
    }

    [Fact]
    public void OnlyAGroup_TakesMembers_AndATitle()
    {
        var direct = Conversation.CreateDirect("p", "a", "b", Now);

        Assert.Throws<DomainException>(() => direct.AddMember("c", ConversationRole.Member, Now));
        Assert.Throws<DomainException>(() => direct.Rename("T"));
    }

    [Fact]
    public void AMemberIsInAGroupOnce()
    {
        var group = Conversation.CreateGroup("p", "T", "lan", Now);
        group.AddMember("minh", ConversationRole.Member, Now);

        Assert.Throws<DomainException>(() => group.AddMember("minh", ConversationRole.Member, Now));
        Assert.Throws<DomainException>(() => group.AddMember("lan", ConversationRole.Member, Now));
    }

    [Fact]
    public void TheReadTime_OnlyMovesForward_AndStartsAtTheJoin()
    {
        var member = ConversationMember.Create(Guid.NewGuid(), "lan", ConversationRole.Member, Now);
        Assert.Equal(Now, member.LastReadAt);

        member.MarkRead(Now.AddMinutes(5));
        member.MarkRead(Now.AddMinutes(1));

        Assert.Equal(Now.AddMinutes(5), member.LastReadAt);
    }

    [Fact]
    public void AMessage_NeedsTextOrAFile_AndTheTypeFollowsTheFile()
    {
        var id = Guid.NewGuid();

        Assert.Throws<DomainException>(() => ChatMessage.Create(id, "lan", "Lan", "  ", null, Now));
        Assert.Throws<DomainException>(() => ChatMessage.Create(id, "lan", "Lan", new string('a', 4001), null, Now));
        Assert.Equal(ChatMessageType.Text, ChatMessage.Create(id, "lan", "Lan", "hi", null, Now).MessageType);
        Assert.Equal(ChatMessageType.Media, ChatMessage.Create(id, "lan", "Lan", "", Guid.NewGuid(), Now).MessageType);
        Assert.Equal(ChatMessageType.System, ChatMessage.System(id, "x joined", Now).MessageType);
    }

    [Fact]
    public void ASenderName_FallsBackToTheId_AndIsClipped()
    {
        var id = Guid.NewGuid();

        Assert.Equal("lan", ChatMessage.Create(id, "lan", "  ", "hi", null, Now).SenderName);
        Assert.Equal(100, ChatMessage.Create(id, "lan", new string('n', 300), "hi", null, Now).SenderName.Length);
    }

    [Fact]
    public void AMemberProfile_IsClipped_AndABlankDoesNotEraseWhatIsThere()
    {
        var member = ConversationMember.Create(Guid.NewGuid(), "lan", ConversationRole.Member, Now);
        member.SetProfile("Lan", "lan@example.com", "chat-1");

        member.SetProfile("  ", null, "");
        member.SetProfile(new string('n', 300), new string('e', 300), new string('c', 300));

        Assert.Equal((100, 200, 200), (member.DisplayName!.Length, member.Email!.Length, member.ChatId!.Length));
    }

    [Fact]
    public void TheChatKey_SeparatesTheSameIdOfTwoProjects()
    {
        Assert.NotEqual(ChatKeys.Of("crm", "lan"), ChatKeys.Of("linguabridge", "lan"));
        Assert.Equal("crm:lan", new ChatActor("crm", "lan", "Lan").Key);
    }
}

public class UserPresenceTrackerTests
{
    [Fact]
    public void AUserIsOnline_WhileOneConnectionIsOpen_OnlyTheFirstAndTheLastReportAChange()
    {
        var tracker = new UserPresenceTracker();

        var first = tracker.Connected("crm", "lan", "tab1");
        var second = tracker.Connected("crm", "lan", "tab2");
        var closeOne = tracker.Disconnected("tab1");
        var stillOnline = tracker.IsOnline("crm", "lan");
        var closeLast = tracker.Disconnected("tab2");

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.False(closeOne.Changed);
        Assert.True(stillOnline);
        Assert.True(closeLast.Changed);
        Assert.False(closeLast.IsOnline);
        Assert.Equal(("crm", "lan"), (closeLast.Project, closeLast.UserId));
        Assert.False(tracker.IsOnline("crm", "lan"));
    }

    [Fact]
    public void TheSameConnectionTwice_IsNotAnotherConnection()
    {
        var tracker = new UserPresenceTracker();

        tracker.Connected("crm", "lan", "c1");
        var repeat = tracker.Connected("crm", "lan", "c1");
        var close = tracker.Disconnected("c1");

        Assert.False(repeat.Changed);
        Assert.True(close.Changed);
        Assert.Equal(0, tracker.ConnectionCount(null));
    }

    [Fact]
    public void AnUnknownConnection_ChangesNothing()
    {
        var tracker = new UserPresenceTracker();
        tracker.Connected("crm", "lan", "c1");

        var change = tracker.Disconnected("never-seen");

        Assert.False(change.Changed);
        Assert.True(tracker.IsOnline("crm", "lan"));
    }

    [Fact]
    public void TheSameIdInTwoProjects_AreTwoUsers()
    {
        var tracker = new UserPresenceTracker();

        tracker.Connected("crm", "lan", "c1");

        Assert.True(tracker.IsOnline("crm", "lan"));
        Assert.False(tracker.IsOnline("linguabridge", "lan"));
    }

    [Fact]
    public void TheOnlineUsers_AreListedPerProject_OrForAllWithTheirProject()
    {
        var tracker = new UserPresenceTracker();
        tracker.Connected("crm", "minh", "c1");
        tracker.Connected("crm", "lan", "c2");
        tracker.Connected("crm", "lan", "c3");
        tracker.Connected("linguabridge", "an", "c4");

        Assert.Equal(["lan", "minh"], tracker.OnlineUsers("crm"));
        Assert.Equal(["crm:lan", "crm:minh", "linguabridge:an"], tracker.OnlineUsers(null));
        Assert.Equal((3, 4), (tracker.ConnectionCount("crm"), tracker.ConnectionCount(null)));
        Assert.Empty(tracker.OnlineUsers("nobody"));
    }

    [Fact]
    public async Task ManyConnectionsAtOnce_LeaveACorrectCount()
    {
        var tracker = new UserPresenceTracker();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => tracker.Connected("crm", "u" + (i % 10), "c" + i))));
        Assert.Equal((200, 10), (tracker.ConnectionCount(null), tracker.OnlineUsers(null).Count));

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => tracker.Disconnected("c" + i))));
        Assert.Equal((0, 0), (tracker.ConnectionCount(null), tracker.OnlineUsers(null).Count));
    }
}
